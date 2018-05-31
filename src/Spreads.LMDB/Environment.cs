﻿// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at http://mozilla.org/MPL/2.0/.

using Spreads.LMDB.Interop;
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Spreads.LMDB
{
    /// <summary>
    /// LMDB Environment.
    /// </summary>
    public class Environment : IDisposable
    {
        private readonly UnixAccessMode _accessMode;
        private readonly DbEnvironmentFlags _openFlags;
        internal EnvironmentHandle _handle;
        private int _maxDbs;
        private int _pageSize;

        internal readonly BlockingCollection<(TaskCompletionSource<object>, Func<Transaction, object>)>
           WriteQueue = new BlockingCollection<(TaskCompletionSource<object>, Func<Transaction, object>)>();

        private readonly Task _writeTask;
        private readonly CancellationTokenSource _cts;
        private readonly string _directory;
        private bool _isOpen;

        /// <summary>
        /// Creates a new instance of Environment.
        /// </summary>
        /// <param name="directory">Relative directory for storing database files.</param>
        /// <param name="openFlags">Database open options.</param>
        /// <param name="accessMode">Unix file access privelegies (optional). Only makes sense on unix operationg systems.</param>
        public Environment(string directory = null,
            DbEnvironmentFlags openFlags = DbEnvironmentFlags.None,
            UnixAccessMode accessMode = UnixAccessMode.Default)
        {
            // we need NoTLS to work well with .NET Tasks, see docs about writers that need a dedicated thread
            openFlags = openFlags | DbEnvironmentFlags.NoTls;

            // this is machine-local storage for each user.
            if (string.IsNullOrWhiteSpace(directory))
            {
                directory = Config.DbEnvironment.DefaultLocation;
            }
            if (!System.IO.Directory.Exists(directory))
            {
                System.IO.Directory.CreateDirectory(directory);
            }

            NativeMethods.AssertExecute(NativeMethods.mdb_env_create(out var envHandle));
            _handle = envHandle;
            _accessMode = accessMode;

            _directory = directory;
            _openFlags = openFlags;

            MaxDatabases = Config.DbEnvironment.DefaultMaxDatabases;

            // Writer Task
            // In the current process writes are serialized via the blocking queue
            // Accross processes, writes are synchronized via WriteTxnGate (TODO!)
            _cts = new CancellationTokenSource();

            _writeTask = Task.Factory.StartNew(() =>
            {
                while (!WriteQueue.IsCompleted)
                {
                    // BLOCKING
                    var tuple = WriteQueue.Take(_cts.Token);
                    var tcs = tuple.Item1;
                    var func = tuple.Item2;
                    try
                    {
                        using (var txn = Transaction.Create(this, TransactionBeginFlags.ReadWrite))
                        {
                            var res = func(txn);
                            tcs.SetResult(res);
                        }
                    }
                    catch (Exception e)
                    {
                        tcs.SetException(e);
                    }
                }
            }, _cts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        /// <summary>
        /// Open the environment.
        /// </summary>
        public void Open()
        {
            if (!System.IO.Directory.Exists(_directory))
            {
                System.IO.Directory.CreateDirectory(_directory);
            }

            if (!_isOpen)
            {
                NativeMethods.AssertExecute(NativeMethods.mdb_env_open(_handle, _directory, _openFlags, _accessMode));
            }

            _isOpen = true;
        }

        /// <summary>
        /// Performs a write trasaction asynchronously.
        /// </summary>
        public Task<object> WriteAsync(Func<Transaction, object> writeAction)
        {
            var builder = new TaskCompletionSource<object>();
            if (!WriteQueue.IsAddingCompleted)
            {
                WriteQueue.Add((builder, writeAction));
            }
            else
            {
                builder.SetException(new OperationCanceledException());
            }
            return builder.Task;
        }

        /// <summary>
        /// Perform a read transaction.
        /// </summary>
        public T Read<T>(Func<Transaction, T> readJob)
        {
            using (var txn = Transaction.Create(this, TransactionBeginFlags.ReadOnly))
            {
                return readJob(txn);
            }
        }

        public async Task<Database> OpenDatabase(string name, DatabaseConfig config)
        {
            return (Database)(await WriteAsync(txn => new Database(name, txn, config)));
        }

        /// <summary>
        /// Close the environment and release the memory map.
        /// Only a single thread may call this function. All transactions, databases, and cursors must already be closed before calling this function.
        /// Attempts to use any such handles after calling this function will cause a SIGSEGV.
        /// The environment handle will be freed and must not be used again after this call.
        /// </summary>
        public async Task Close()
        {
            if (!_isOpen) return;

            // let finish already added write tasks
            WriteQueue.CompleteAdding();
            await _writeTask;
            _cts.Cancel();
            NativeMethods.mdb_env_close(_handle);
            _handle.Dispose();
            _isOpen = false;
        }

        public MDB_stat GetStat()
        {
            EnsureOpened();
            NativeMethods.AssertRead(NativeMethods.mdb_env_stat(_handle, out var stat));
            return stat;
        }

        /// <summary>
        /// Number of entires in the main database
        /// </summary>
        public long GetEntriesCount()
        {
            var stat = GetStat();
            return stat.ms_entries.ToInt64();
        }

        public long GetUsedSize()
        {
            var stat = GetStat();
            var totalPages =
                stat.ms_branch_pages.ToInt64() +
                stat.ms_leaf_pages.ToInt64() +
                stat.ms_overflow_pages.ToInt64();
            return stat.ms_psize * totalPages;
        }

        public MDB_envinfo GetEnvInfo()
        {
            EnsureOpened();
            NativeMethods.AssertExecute(NativeMethods.mdb_env_info(_handle, out var info));
            return info;
        }

        /// <summary>
        /// Whether the environment is opened.
        /// </summary>
        public bool IsOpen => _isOpen;

        /// Set the size of the memory map to use for this environment.
        /// The size should be a multiple of the OS page size.
        /// The default is 10485760 bytes.
        /// The size of the memory map is also the maximum size of the database.
        /// The value should be chosen as large as possible, to accommodate future growth of the database.
        /// This function may only be called before the environment is opened.
        /// The size may be changed by closing and reopening the environment.
        /// Any attempt to set a size smaller than the space already consumed by the environment will be silently changed to the current size of the used space.
        public long MapSize
        {
            get
            {
                var info = GetEnvInfo();
                return info.me_mapsize.ToInt64();
            }
            set
            {
                if (_isOpen)
                {
                    throw new InvalidOperationException("Can't change MapSize of opened environment");
                }
                NativeMethods.AssertExecute(NativeMethods.mdb_env_set_mapsize(_handle, (IntPtr)value));
            }
        }

        public int PageSize
        {
            get
            {
                if (_pageSize == 0)
                {
                    var stat = GetStat();
                    _pageSize = (int)stat.ms_psize;
                }
                return _pageSize;
            }
        }

        /// <summary>
        /// Last used page of the environment multiplied by its page size.
        /// </summary>
        public long UsedSize
        {
            get
            {
                Flush(true);
                var info = GetEnvInfo();
                return info.me_last_pgno.ToInt32() * PageSize;
            }
        }

        /// <summary>
        /// Get the maximum number of threads for the environment.
        /// </summary>
        public int MaxReaders
        {
            get
            {
                NativeMethods.AssertExecute(NativeMethods.mdb_env_get_maxreaders(_handle, out var readers));
                return (int)readers;
            }
            set
            {
                if (_isOpen)
                {
                    throw new InvalidOperationException("Can't change MaxReaders of opened environment");
                }
                NativeMethods.AssertExecute(NativeMethods.mdb_env_set_maxreaders(_handle, (uint)value));
            }
        }

        public int MaxKeySize => NativeMethods.mdb_env_get_maxkeysize(_handle);

        /// <summary>
        /// Set the maximum number of named databases for the environment.
        /// This function is only needed if multiple databases will be used in the environment.
        /// Simpler applications that use the environment as a single unnamed database can ignore this option.
        /// This function may only be called before the environment is opened.
        /// </summary>
        public int MaxDatabases
        {
            get => _maxDbs;
            set
            {
                if (_isOpen)
                {
                    throw new InvalidOperationException("Can't change MaxDatabases of opened environment");
                }
                if (value == _maxDbs) return;
                NativeMethods.AssertExecute(NativeMethods.mdb_env_set_maxdbs(_handle, (uint)value));
                _maxDbs = value;
            }
        }

        public long EntriesCount { get { return GetStat().ms_entries.ToInt64(); } }

        /// <summary>
        /// Directory path to store database files.
        /// </summary>
        public string Directory => _directory;


        /// <summary>
        /// Create a transaction for use with the environment.
        /// The transaction object MUST be disposed after reading or after Abort() or Commit().
        /// </summary>
        public Transaction ReadTransaction()
        {
            return Transaction.Create(this, TransactionBeginFlags.ReadOnly);
        }

        /// <summary>
        /// Copy an MDB environment to the specified path.
        /// This function may be used to make a backup of an existing environment.
        /// </summary>
        /// <param name="path">The directory in which the copy will reside. This directory must already exist and be writable but must otherwise be empty.</param>
        /// <param name="compact">Omit empty pages when copying.</param>
        public void CopyTo(string path, bool compact = false)
        {
            EnsureOpened();
            var flags = compact ? EnvironmentCopyFlags.Compact : EnvironmentCopyFlags.None;
            NativeMethods.AssertExecute(NativeMethods.mdb_env_copy2(_handle, path, flags));
        }

        /// <summary>
        /// Flush the data buffers to disk.
        /// Data is always written to disk when LightningTransaction.Commit is called, but the operating system may keep it buffered.
        /// MDB always flushes the OS buffers upon commit as well, unless the environment was opened with EnvironmentOpenFlags.NoSync or in part EnvironmentOpenFlags.NoMetaSync.
        /// </summary>
        /// <param name="force">If true, force a synchronous flush. Otherwise if the environment has the EnvironmentOpenFlags.NoSync flag set the flushes will be omitted, and with MDB_MAPASYNC they will be asynchronous.</param>
        public void Flush(bool force)
        {
            NativeMethods.AssertExecute(NativeMethods.mdb_env_sync(_handle, force));
        }

        internal void EnsureOpened()
        {
            if (!_isOpen)
            {
                throw new InvalidOperationException("Environment should be opened");
            }
        }

        protected virtual void Dispose(bool disposing)
        {
            Close().Wait();
        }

        /// <summary>
        /// Dispose the environment and release the memory map.
        /// Only a single thread may call this function. All transactions, databases, and cursors must already be closed before calling this function.
        /// Attempts to use any such handles after calling this function will cause a SIGSEGV.
        /// The environment handle will be freed and must not be used again after this call.
        /// </summary>
        public void Dispose()
        {
            GC.SuppressFinalize(this);
            Dispose(true);
        }

        ~Environment()
        {
            Dispose(false);
        }
    }
}