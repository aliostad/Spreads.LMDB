|   Linux   |  Windows  |    Mac    | 
|:---------:|:---------:|:---------:|
| [![Build Status](https://dev.azure.com/DataSpreads/Spreads.LMDB/_apis/build/status/Spreads.Spreads.LMDB?branchName=master&jobName=Linux)](https://dev.azure.com/DataSpreads/Spreads.LMDB/_build/latest?definitionId=3&branchName=master) | [![Build Status](https://dev.azure.com/DataSpreads/Spreads.LMDB/_apis/build/status/Spreads.Spreads.LMDB?branchName=master&jobName=Windows)](https://dev.azure.com/DataSpreads/Spreads.LMDB/_build/latest?definitionId=3&branchName=master) | [![Build Status](https://dev.azure.com/DataSpreads/Spreads.LMDB/_apis/build/status/Spreads.Spreads.LMDB?branchName=master&jobName=Mac)](https://dev.azure.com/DataSpreads/Spreads.LMDB/_build/latest?definitionId=3&branchName=master) |


# Spreads.LMDB

Low-level zero-overhead and [the fastest](https://github.com/Spreads/Spreads.LMDB/commit/4085dde649ef9ebb64310f2627299762dd62d5ce) LMDB .NET wrapper with some additional native 
methods useful for [Spreads](https://github.com/Spreads/).

Available on NuGet as [Spreads.LMDB](https://www.nuget.org/packages/Spreads.LMDB).

## C# `async/await` support

LMDB's supported "normal" case is when a transaction is executed from a single thread. For .NET this means 
that if all operations on a transactions are called from a single thread it doesn't matter which
thread is executing a transaction and LMDB will just work.

In some cases one my need background execution of write transactions or .NET async operations inside LMDB transactions. For this case Spreads.LMDB
fully supports async/await. Write transactions are executed in a single thread via a blocking concurrent queue. Read transactions could be used from async code, which requires forcing [`MDB_NOTLS`](http://www.lmdb.tech/doc/group__mdb.html#ga32a193c6bf4d7d5c5d579e71f22e9340) 
attribute for environments:

> A thread may use parallel read-only transactions. A read-only transaction may span threads if the user synchronizes its use. Applications that multiplex many user threads over individual OS threads need this option. Such an application must also serialize the write transactions in an OS thread, since LMDB's write locking is unaware of the user threads.

Async support is **disabled** by default, but could be turned on 
via `LMDBEnvironment.Create(..., disableAsync: false);` if needed.

## Read-only transaction and cursor renewal

Spreads.LMDB automatically takes care of read-only transaction and cursor renewals 
if they are properly disposed as .NET objects. It does not allocate those 
objects in steady state (uses internal pools).

## Working with memory safely

**Warning!** This library exposes `MDB_val` directly as `DirectBuffer` struct, the struct *MUST ONLY* be read when inside a transaction
(or when it points to an overflow page - but that is an undocumented hack working so far). For writes, 
the memory behind `DirectBuffer` *MUST BE pinned*. 

`DirectBuffer.Span` property allows to access `MDB_val` as `Span<byte>`. `DirectBuffer` can be easily constructed from `Span<byte>`, 
but the span must be pinned as well if it is backed by `byte[]`.

[`DirectBuffer`](https://github.com/Spreads/Spreads/blob/master/src/Spreads.Core/Buffers/DirectBuffer.cs) has many methods
 to read/write primitive and generic blittable struct values from any offset, 
e.g. `directBufferInstance.Read<ulong>(8)` to read `ulong` from offset `8`. By default
it checks bounds, and LMDB call via P/Invoke takes much longer so there is no reason to switch the 
bounds checks off, but you can still do so e.g. if you read separate bytes of large values
 a lot (e.g. via indexer `directBufferInstance[offset]` that returns a single byte at `offset`).

## Generic key/values support

Any fixed-sized `unmanaged` structs could be used directly as keys/values. Until `unmanaged`
constraint and blittable helpers (at least `IsBlittable`) are widly available we use
opt-in to treat a *custom user-defined* struct as blittable. It must have defined Spreads' 
[`BinarySerializationAttribute`](https://github.com/Spreads/Spreads/blob/master/src/Spreads.Core/Serialization/BinarySerializationAttribute.cs)
with `BlittableSize` parameter for non-generic types or `PreferBlittable` set to `true`
for generic types that could be blittable depending on a concrete type. The logic to decide
if a type is fixed-size is in [TypeHelper<T>](https://github.com/Spreads/Spreads/blob/master/src/Spreads.Core/Serialization/TypeHelper.cs)
and its `TypeHelper<T>.Size` static property must be positive.

## IEnumerable support

A database or duplicate values of a key in a single dupsorted database could be enumerated via `dataVaseInstance.AsEnumerable([several overloads])` methods that could return 
either `DirectBuffer`s or generic blittable structs.

# Examples

Tests show how to use the code.

# Status & limitations

This library is being deployed and tested in production and is went through many performance 
and correctness stress tests as a part of a larger workload.

The project has required native binaries and source in [`lib`](https://github.com/Spreads/Spreads.LMDB/tree/master/lib) folder. 
Binaries are native shared libraries compressed with 
`deflate` and embedded into the package dll as resources (this often simplifies deployment). 
The library works with original native binaries as well if not using two `TryFind` helper methods.

The library does not support nested transactions yet - only because we do not use them currently. 
They will be added as soon as we find a real-world compelling case for them. 


# Contributing

Issues & PRs are welcome!

# Copyright

MPL 2.0
(c) Victor Baybekov, 2018

