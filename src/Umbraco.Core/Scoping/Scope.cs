﻿using System;
using System.Collections.Generic;
using System.Data;
using Umbraco.Core.Cache;
using Umbraco.Core.Composing;
using Umbraco.Core.Events;
using Umbraco.Core.IO;
using Umbraco.Core.Logging;
using Umbraco.Core.Persistence;
using Umbraco.Core.Persistence.SqlSyntax;

namespace Umbraco.Core.Scoping
{
    /// <summary>
    /// Implements <see cref="IScope"/>.
    /// </summary>
    /// <remarks>Not thread-safe obviously.</remarks>
    internal class Scope : IScope2
    {
        private readonly ScopeProvider _scopeProvider;
        private readonly ILogger _logger;

        private readonly IsolationLevel _isolationLevel;
        private readonly RepositoryCacheMode _repositoryCacheMode;
        private readonly bool? _scopeFileSystem;
        private readonly bool _autoComplete;
        private bool _callContext;

        private bool _disposed;
        private bool? _completed;

        private IsolatedCaches _isolatedCaches;
        private IUmbracoDatabase _database;
        private EventMessages _messages;
        private ICompletable _fscope;
        private IEventDispatcher _eventDispatcher;

        // ReadLocks and WriteLocks owned by the entire chain, managed by the outermost scope
        public Dictionary<int, int> ReadLocks;
        public Dictionary<int, int> WriteLocks;

        // ReadLocks and WriteLocks requested by this specific scope, required to be able to decrement
        // Using Lists just in case someone for some reason requests the same lock twice in the same scope.
        private List<int> _readLocks;
        private List<int> _writelocks;

        // initializes a new scope
        private Scope(ScopeProvider scopeProvider,
            ILogger logger, FileSystems fileSystems, Scope parent, ScopeContext scopeContext, bool detachable,
            IsolationLevel isolationLevel = IsolationLevel.Unspecified,
            RepositoryCacheMode repositoryCacheMode = RepositoryCacheMode.Unspecified,
            IEventDispatcher eventDispatcher = null,
            bool? scopeFileSystems = null,
            bool callContext = false,
            bool autoComplete = false)
        {
            _scopeProvider = scopeProvider;
            _logger = logger;

            Context = scopeContext;

            _isolationLevel = isolationLevel;
            _repositoryCacheMode = repositoryCacheMode;
            _eventDispatcher = eventDispatcher;
            _scopeFileSystem = scopeFileSystems;
            _callContext = callContext;
            _autoComplete = autoComplete;

            Detachable = detachable;

            ReadLocks = new Dictionary<int, int>();
            WriteLocks = new Dictionary<int, int>();
            _readLocks = new List<int>();
            _writelocks = new List<int>();

#if DEBUG_SCOPES
            _scopeProvider.RegisterScope(this);
            Console.WriteLine("create " + InstanceId.ToString("N").Substring(0, 8));
#endif

            if (detachable)
            {
                if (parent != null) throw new ArgumentException("Cannot set parent on detachable scope.", nameof(parent));
                if (scopeContext != null) throw new ArgumentException("Cannot set context on detachable scope.", nameof(scopeContext));
                if (autoComplete) throw new ArgumentException("Cannot auto-complete a detachable scope.", nameof(autoComplete));

                // detachable creates its own scope context
                Context = new ScopeContext();

                // see note below
                if (scopeFileSystems == true)
                    _fscope = fileSystems.Shadow();

                return;
            }

            if (parent != null)
            {
                ParentScope = parent;

                // cannot specify a different mode!
                // TODO: means that it's OK to go from L2 to None for reading purposes, but writing would be BAD!
                // this is for XmlStore that wants to bypass caches when rebuilding XML (same for NuCache)
                if (repositoryCacheMode != RepositoryCacheMode.Unspecified && parent.RepositoryCacheMode > repositoryCacheMode)
                    throw new ArgumentException($"Value '{repositoryCacheMode}' cannot be lower than parent value '{parent.RepositoryCacheMode}'.", nameof(repositoryCacheMode));

                // cannot specify a dispatcher!
                if (_eventDispatcher != null)
                    throw new ArgumentException("Value cannot be specified on nested scope.", nameof(eventDispatcher));

                // cannot specify a different fs scope!
                // can be 'true' only on outer scope (and false does not make much sense)
                if (scopeFileSystems != null && parent._scopeFileSystem != scopeFileSystems)
                    throw new ArgumentException($"Value '{scopeFileSystems.Value}' be different from parent value '{parent._scopeFileSystem}'.", nameof(scopeFileSystems));
            }
            else
            {
                // the FS scope cannot be "on demand" like the rest, because we would need to hook into
                // every scoped FS to trigger the creation of shadow FS "on demand", and that would be
                // pretty pointless since if scopeFileSystems is true, we *know* we want to shadow
                if (scopeFileSystems == true)
                    _fscope = fileSystems.Shadow();
            }
        }

        // initializes a new scope
        public Scope(ScopeProvider scopeProvider,
            ILogger logger, FileSystems fileSystems, bool detachable, ScopeContext scopeContext,
            IsolationLevel isolationLevel = IsolationLevel.Unspecified,
            RepositoryCacheMode repositoryCacheMode = RepositoryCacheMode.Unspecified,
            IEventDispatcher eventDispatcher = null,
            bool? scopeFileSystems = null,
            bool callContext = false,
            bool autoComplete = false)
            : this(scopeProvider, logger, fileSystems, null, scopeContext, detachable, isolationLevel, repositoryCacheMode, eventDispatcher, scopeFileSystems, callContext, autoComplete)
        { }

        // initializes a new scope in a nested scopes chain, with its parent
        public Scope(ScopeProvider scopeProvider,
            ILogger logger, FileSystems fileSystems, Scope parent,
            IsolationLevel isolationLevel = IsolationLevel.Unspecified,
            RepositoryCacheMode repositoryCacheMode = RepositoryCacheMode.Unspecified,
            IEventDispatcher eventDispatcher = null,
            bool? scopeFileSystems = null,
            bool callContext = false,
            bool autoComplete = false)
            : this(scopeProvider, logger, fileSystems, parent, null, false, isolationLevel, repositoryCacheMode, eventDispatcher, scopeFileSystems, callContext, autoComplete)
        { }

        public Guid InstanceId { get; } = Guid.NewGuid();

        public ISqlContext SqlContext => _scopeProvider.SqlContext;

        // a value indicating whether to force call-context
        public bool CallContext
        {
            get
            {
                if (_callContext) return true;
                if (ParentScope != null) return ParentScope.CallContext;
                return false;
            }
            set => _callContext = value;
        }

        public bool ScopedFileSystems
        {
            get
            {
                if (ParentScope != null) return ParentScope.ScopedFileSystems;
                return _fscope != null;
            }
        }

        /// <inheritdoc />
        public RepositoryCacheMode RepositoryCacheMode
        {
            get
            {
                if (_repositoryCacheMode != RepositoryCacheMode.Unspecified) return _repositoryCacheMode;
                if (ParentScope != null) return ParentScope.RepositoryCacheMode;
                return RepositoryCacheMode.Default;
            }
        }

        /// <inheritdoc />
        public IsolatedCaches IsolatedCaches
        {
            get
            {
                if (ParentScope != null) return ParentScope.IsolatedCaches;

                return _isolatedCaches ?? (_isolatedCaches
                           = new IsolatedCaches(type => new DeepCloneAppCache(new ObjectCacheAppCache())));
            }
        }

        // a value indicating whether the scope is detachable
        // ie whether it was created by CreateDetachedScope
        public bool Detachable { get; }

        // the parent scope (in a nested scopes chain)
        public Scope ParentScope { get; set; }

        public bool Attached { get; set; }

        // the original scope (when attaching a detachable scope)
        public Scope OrigScope { get; set; }

        // the original context (when attaching a detachable scope)
        public ScopeContext OrigContext { get; set; }

        // the context (for attaching & detaching only)
        public ScopeContext Context { get; }

        public IsolationLevel IsolationLevel
        {
            get
            {
                if (_isolationLevel != IsolationLevel.Unspecified) return _isolationLevel;
                if (ParentScope != null) return ParentScope.IsolationLevel;
                return Database.SqlContext.SqlSyntax.DefaultIsolationLevel;
            }
        }

        /// <inheritdoc />
        public IUmbracoDatabase Database
        {
            get
            {
                EnsureNotDisposed();

                if (_database != null)
                    return _database;

                if (ParentScope != null)
                {
                    var database = ParentScope.Database;
                    var currentLevel = database.GetCurrentTransactionIsolationLevel();
                    if (_isolationLevel > IsolationLevel.Unspecified && currentLevel < _isolationLevel)
                        throw new Exception("Scope requires isolation level " + _isolationLevel + ", but got " + currentLevel + " from parent.");
                    return _database = database;
                }

                // create a new database
                _database = _scopeProvider.DatabaseFactory.CreateDatabase();

                // enter a transaction, as a scope implies a transaction, always
                try
                {
                    _database.BeginTransaction(IsolationLevel);
                    return _database;
                }
                catch
                {
                    _database.Dispose();
                    _database = null;
                    throw;
                }
            }
        }

        public IUmbracoDatabase DatabaseOrNull
        {
            get
            {
                EnsureNotDisposed();
                return ParentScope == null ? _database : ParentScope.DatabaseOrNull;
            }
        }

        /// <inheritdoc />
        public EventMessages Messages
        {
            get
            {
                EnsureNotDisposed();
                if (ParentScope != null) return ParentScope.Messages;
                return _messages ?? (_messages = new EventMessages());

                // TODO: event messages?
                // this may be a problem: the messages collection will be cleared at the end of the scope
                // how shall we process it in controllers etc? if we don't want the global factory from v7?
                // it'd need to be captured by the controller
                //
                // + rename // EventMessages = ServiceMessages or something
            }
        }

        public EventMessages MessagesOrNull
        {
            get
            {
                EnsureNotDisposed();
                return ParentScope == null ? _messages : ParentScope.MessagesOrNull;
            }
        }

        /// <inheritdoc />
        public IEventDispatcher Events
        {
            get
            {
                EnsureNotDisposed();
                if (ParentScope != null) return ParentScope.Events;
                return _eventDispatcher ?? (_eventDispatcher = new QueuingEventDispatcher());
            }
        }

        /// <inheritdoc />
        public bool Complete()
        {
            if (_completed.HasValue == false)
                _completed = true;
            return _completed.Value;
        }

        public void Reset()
        {
            _completed = null;
        }

        public void ChildCompleted(bool? completed)
        {
            // if child did not complete we cannot complete
            if (completed.HasValue == false || completed.Value == false)
            {
                if (LogUncompletedScopes)
                    _logger.Debug<Scope>("Uncompleted Child Scope at\r\n {StackTrace}", Environment.StackTrace);

                _completed = false;
            }
        }

        private void EnsureNotDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(GetType().FullName);

            // TODO: safer?
            //if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
            //    throw new ObjectDisposedException(GetType().FullName);
        }

        public void Dispose()
        {
            EnsureNotDisposed();

            if (this != _scopeProvider.AmbientScope)
            {
#if DEBUG_SCOPES
                var ambient = _scopeProvider.AmbientScope;
                _logger.Debug<Scope>("Dispose error (" + (ambient == null ? "no" : "other") + " ambient)");
                if (ambient == null)
                    throw new InvalidOperationException("Not the ambient scope (no ambient scope).");
                var ambientInfos = _scopeProvider.GetScopeInfo(ambient);
                var disposeInfos = _scopeProvider.GetScopeInfo(this);
                throw new InvalidOperationException("Not the ambient scope (see ctor stack traces).\r\n"
                    + "- ambient ctor ->\r\n" + ambientInfos.CtorStack + "\r\n"
                    + "- dispose ctor ->\r\n" + disposeInfos.CtorStack + "\r\n");
#else
                throw new InvalidOperationException("Not the ambient scope.");
#endif
            }

            // Decrement the lock counters
            foreach (var readLockId in _readLocks)
            {
                DecrementReadLock(readLockId);
            }

            foreach (var writeLockId in _writelocks)
            {
                DecrementWriteLock(writeLockId);
            }

            var parent = ParentScope;
            _scopeProvider.AmbientScope = parent; // might be null = this is how scopes are removed from context objects

#if DEBUG_SCOPES
            _scopeProvider.Disposed(this);
#endif

            if (_autoComplete && _completed == null)
                _completed = true;

            if (parent != null)
                parent.ChildCompleted(_completed);
            else
                DisposeLastScope();

            _disposed = true;
            GC.SuppressFinalize(this);
        }

        private void DisposeLastScope()
        {
            // figure out completed
            var completed = _completed.HasValue && _completed.Value;

            // deal with database
            var databaseException = false;
            if (_database != null)
            {
                try
                {
                    if (completed)
                        _database.CompleteTransaction();
                    else
                        _database.AbortTransaction();
                }
                catch
                {
                    databaseException = true;
                    throw;
                }
                finally
                {
                    _database.Dispose();
                    _database = null;

                    if (databaseException)
                        RobustExit(false, true);
                }
            }

            RobustExit(completed, false);
        }

        // this chains some try/finally blocks to
        // - complete and dispose the scoped filesystems
        // - deal with events if appropriate
        // - remove the scope context if it belongs to this scope
        // - deal with detachable scopes
        // here,
        // - completed indicates whether the scope has been completed
        //    can be true or false, but in both cases the scope is exiting
        //    in a normal way
        // - onException indicates whether completing/aborting the database
        //    transaction threw an exception, in which case 'completed' has
        //    to be false + events don't trigger and we just to some cleanup
        //    to ensure we don't leave a scope around, etc
        private void RobustExit(bool completed, bool onException)
        {
             if (onException) completed = false;

            TryFinally(() =>
            {
                if (_scopeFileSystem == true)
                {
                    if (completed)
                        _fscope.Complete();
                    _fscope.Dispose();
                    _fscope = null;
                }
            }, () =>
            {
                // deal with events
                if (onException == false)
                    _eventDispatcher?.ScopeExit(completed);
            }, () =>
            {
                // if *we* created it, then get rid of it
                if (_scopeProvider.AmbientContext == Context)
                {
                    try
                    {
                        _scopeProvider.AmbientContext.ScopeExit(completed);
                    }
                    finally
                    {
                        // removes the ambient context (ambient scope already gone)
                        _scopeProvider.SetAmbient(null);
                    }
                }
            }, () =>
            {
                if (Detachable)
                {
                    // get out of the way, restore original
                    _scopeProvider.SetAmbient(OrigScope, OrigContext);
                    Attached = false;
                    OrigScope = null;
                    OrigContext = null;
                }
            });
        }

        private static void TryFinally(params Action[] actions)
        {
            TryFinally(0, actions);
        }

        private static void TryFinally(int index, Action[] actions)
        {
            if (index == actions.Length) return;
            try
            {
                actions[index]();
            }
            finally
            {
                TryFinally(index + 1, actions);
            }
        }

        // backing field for LogUncompletedScopes
        private static bool? _logUncompletedScopes;

        // caching config
        // true if Umbraco.CoreDebug.LogUncompletedScope appSetting is set to "true"
        private static bool LogUncompletedScopes => (_logUncompletedScopes
            ?? (_logUncompletedScopes = Current.Configs.CoreDebug().LogUncompletedScopes)).Value;

        /// <summary>
        /// Decrements the count of the ReadLocks with a specific lock object identifier we currently hold
        /// </summary>
        /// <param name="lockId">Lock object identifier to decrement</param>
        public void DecrementReadLock(int lockId)
        {
            // If we aren't the outermost scope, pass it on to the parent.
            if (ParentScope != null)
            {
                ParentScope.DecrementReadLock(lockId);
                return;
            }

            ReadLocks[lockId] -= 1;
        }

        /// <summary>
        /// Decrements the count of the WriteLocks with a specific lock object identifier we currently hold.
        /// </summary>
        /// <param name="lockId">Lock object identifier to decrement.</param>
        public void DecrementWriteLock(int lockId)
        {
            // If we aren't the outermost scope, pass it on to the parent.
            if (ParentScope != null)
            {
                ParentScope.DecrementWriteLock(lockId);
                return;
            }

            WriteLocks[lockId] -= 1;
        }

        /// <inheritdoc />
        public void ReadLock(params int[] lockIds)
        {
            if (ParentScope != null)
            {
                foreach (var id in lockIds)
                {
                    // We need to keep track of what lockIds we have requested locks for to be able to decrement them.
                    // We have to do this out of the recursion to not add the ids to all scopes.
                    _readLocks.Add(id);
                }
            }
            ReadLockInner(null, lockIds);
        }

        /// <inheritdoc />
        public void ReadLock(TimeSpan timeout, int lockId)
        {
            if (ParentScope != null)
            {
                _readLocks.Add(lockId);
            }

            ReadLockInner(timeout, lockId);
        }

        /// <inheritdoc />
        public void WriteLock(params int[] lockIds)
        {
            if (ParentScope != null)
            {
                foreach (var lockId in lockIds)
                {
                    _writelocks.Add(lockId);
                }
            }
            WriteLockInner(null, lockIds);
        }

        /// <inheritdoc />
        public void WriteLock(TimeSpan timeout, int lockId)
        {
            if (ParentScope != null)
            {
                _writelocks.Add(lockId);
            }
            WriteLockInner(timeout, lockId);
        }

        /// <summary>
        /// Handles acquiring a read lock, will delegate it to the parent if there are any.
        /// </summary>
        /// <param name="timeout">Optional database timeout in milliseconds.</param>
        /// <param name="lockIds">Array of lock object identifiers.</param>
        internal void ReadLockInner(TimeSpan? timeout = null, params int[] lockIds)
        {
            if (ParentScope != null)
            {
                // Delegate acquiring the lock to the parent if any.
                ParentScope.ReadLockInner(timeout, lockIds);
                return;
            }

            // If we are the parent, then handle the lock request.
            foreach (var lockId in lockIds)
            {
                // Only acquire the lock if we haven't done so yet.
                if (!ReadLocks.ContainsKey(lockId))
                {
                    if (timeout is null)
                    {
                        // We want a lock with a custom timeout
                        ObtainReadLock(lockId);
                    }
                    else
                    {
                        // We just want an ordinary lock.
                        ObtainTimoutReadLock(lockId, timeout.Value);
                    }
                    // Add the lockId as a key to the dict.
                    ReadLocks[lockId] = 0;
                }

                ReadLocks[lockId] += 1;
            }
        }

        /// <summary>
        /// Handles acquiring a write lock with a specified timeout, will delegate it to the parent if there are any.
        /// </summary>
        /// <param name="timeout">Optional database timeout in milliseconds.</param>
        /// <param name="lockIds">Array of lock object identifiers.</param>
        internal void WriteLockInner(TimeSpan? timeout = null, params int[] lockIds)
        {
            if (ParentScope != null)
            {
                // If we have a parent we delegate lock creation to parent.
                ParentScope.WriteLockInner(timeout, lockIds);
                return;
            }

            foreach (var lockId in lockIds)
            {
                // Only acquire lock if we haven't yet (WriteLocks not containing the key)
                if (!WriteLocks.ContainsKey(lockId))
                {
                    if (timeout is null)
                    {
                        ObtainWriteLock(lockId);
                    }
                    else
                    {
                        ObtainTimeoutWriteLock(lockId, timeout.Value);
                    }
                    // Add the lockId as a key to the dict.
                    WriteLocks[lockId] = 0;
                }

                // Increment count of the lock by 1.
                WriteLocks[lockId] += 1;
            }
        }

        /// <summary>
        /// Obtains an ordinary read lock.
        /// </summary>
        /// <param name="lockId">Lock object identifier to lock.</param>
        private void ObtainReadLock(int lockId)
        {
            Database.SqlContext.SqlSyntax.ReadLock(Database, lockId);
        }

        /// <summary>
        /// Obtains a read lock with a custom timeout.
        /// </summary>
        /// <param name="lockId">Lock object identifier to lock.</param>
        /// <param name="timeout">TimeSpan specifying the timout period.</param>
        private void ObtainTimoutReadLock(int lockId, TimeSpan timeout)
        {
            var syntax2 = Database.SqlContext.SqlSyntax as ISqlSyntaxProvider2;
            if (syntax2 == null)
            {
                throw new InvalidOperationException($"{Database.SqlContext.SqlSyntax.GetType()} is not of type {typeof(ISqlSyntaxProvider2)}");
            }

            syntax2.ReadLock(Database, timeout, lockId);
        }

        /// <summary>
        /// Obtains an ordinary write lock.
        /// </summary>
        /// <param name="lockId">Lock object identifier to lock.</param>
        private void ObtainWriteLock(int lockId)
        {
            Database.SqlContext.SqlSyntax.WriteLock(Database, lockId);
        }

        /// <summary>
        /// Obtains a write lock with a custom timeout.
        /// </summary>
        /// <param name="lockId">Lock object identifier to lock.</param>
        /// <param name="timeout">TimeSpan specifying the timout period.</param>
        private void ObtainTimeoutWriteLock(int lockId, TimeSpan timeout)
        {
            var syntax2 = Database.SqlContext.SqlSyntax as ISqlSyntaxProvider2;
            if (syntax2 == null)
            {
                throw new InvalidOperationException($"{Database.SqlContext.SqlSyntax.GetType()} is not of type {typeof(ISqlSyntaxProvider2)}");
            }

            syntax2.WriteLock(Database, timeout, lockId);
        }
    }
}
