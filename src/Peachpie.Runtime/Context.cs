﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Pchp.Core.Utilities;
using System.Reflection;
using Pchp.Core.Reflection;

namespace Pchp.Core
{
    /// <summary>
    /// Runtime context for a PHP application.
    /// </summary>
    /// <remarks>
    /// The object represents a current Web request or the application run.
    /// Its instance is passed to all PHP function.
    /// The context is not thread safe.
    /// </remarks>
    [DebuggerNonUserCode]
    public partial class Context : IDisposable
    {
        #region Create

        protected Context()
        {
            // Context tables
            _functions = new RoutinesTable(RoutinesAppContext.NameToIndex, RoutinesAppContext.AppRoutines, RoutinesAppContext.ContextRoutinesCounter, FunctionRedeclared);
            _types = new TypesTable(TypesAppContext.NameToIndex, TypesAppContext.AppTypes, TypesAppContext.ContextTypesCounter, TypeRedeclared);
            _statics = new object[StaticIndexes.StaticsCount];
        }

        /// <summary>
        /// Create default context with no output.
        /// </summary>
        /// <param name="cmdargs">
        /// Optional arguments to be passed to PHP <c>$argv</c> and <c>$argc</c> global variables.
        /// If the array is empty, variables are not created.
        /// </param>
        public static Context CreateEmpty(params string[] cmdargs)
        {
            var ctx = new Context();
            ctx.InitOutput(null);
            ctx.InitSuperglobals();

            if (cmdargs != null && cmdargs.Length != 0)
            {
                ctx.InitializeArgvArgc(cmdargs);
            }

            //
            return ctx;
        }

        #endregion

        #region Symbols

        /// <summary>
        /// Map of global functions.
        /// </summary>
        readonly RoutinesTable _functions;

        /// <summary>
        /// Map of global types.
        /// </summary>
        readonly TypesTable _types;

        /// <summary>
        /// Map of global constants.
        /// </summary>
        readonly ConstsMap _constants = new ConstsMap();

        readonly ScriptsMap _scripts = new ScriptsMap();

        /// <summary>
        /// Internal method to be used by loader to load referenced symbols.
        /// </summary>
        /// <typeparam name="TScript"><c>&lt;Script&gt;</c> type in compiled assembly. The type contains static methods for enumerating referenced symbols.</typeparam>
        public static void AddScriptReference<TScript>() => AddScriptReference(typeof(TScript));

        /// <summary>
        /// Load PHP scripts and referenced symbols from PHP assembly.
        /// </summary>
        /// <param name="assembly">PHP assembly containing special <see cref="ScriptInfo.ScriptTypeName"/> class.</param>
        public static void AddScriptReference(Assembly assembly)
        {
            if (assembly == null)
            {
                throw new ArgumentNullException("assembly");
            }

            var t = assembly.GetType(ScriptInfo.ScriptTypeName);
            if (t != null)
            {
                AddScriptReference(t);
            }
        }

        /// <summary>
        /// Reflects given <c>&lt;Script&gt;</c> type generated by compiler to load list of its symbols
        /// and make them available to runtime.
        /// </summary>
        /// <param name="tscript"><c>&lt;Script&gt;</c> type from compiled assembly.</param>
        protected static void AddScriptReference(Type tscript)
        {
            Debug.Assert(tscript != null);
            Debug.Assert(tscript.Name == ScriptInfo.ScriptTypeName);

            var tscriptinfo = tscript.GetTypeInfo();

            tscriptinfo.GetDeclaredMethod("EnumerateReferencedFunctions")
                .Invoke(null, new object[] { new Action<string, RuntimeMethodHandle>(RoutinesAppContext.DeclareRoutine) });

            tscriptinfo.GetDeclaredMethod("EnumerateReferencedTypes")
                .Invoke(null, new object[] { new Action<string, RuntimeTypeHandle>(TypesAppContext.DeclareType) });

            tscriptinfo.GetDeclaredMethod("EnumerateScripts")
                .Invoke(null, new object[] { new Action<string, RuntimeMethodHandle>(ScriptsMap.DeclareScript) });

            tscriptinfo.GetDeclaredMethod("EnumerateConstants")
                .Invoke(null, new object[] { new Action<string, PhpValue, bool>(ConstsMap.DefineAppConstant) });

            //
            ScriptAdded(tscriptinfo);
        }

        static void ScriptAdded(TypeInfo tscript)
        {
            Debug.Assert(tscript != null);

            if (_targetPhpLanguageAttribute == null)
            {
                _targetPhpLanguageAttribute = tscript.Assembly.GetCustomAttribute<TargetPhpLanguageAttribute>();
            }
        }

        /// <summary>
        /// Declare a runtime user function.
        /// </summary>
        public void DeclareFunction(RoutineInfo routine) => _functions.DeclarePhpRoutine(routine);

        public void AssertFunctionDeclared(RoutineInfo routine)
        {
            if (!_functions.IsDeclared(routine))
            {
                // TODO: ErrCode function is not declared
            }
        }

        /// <summary>
        /// Internal. Used by callsites cache to check whether called function is the same as the one declared.
        /// </summary>
        internal bool CheckFunctionDeclared(int index, int expectedHashCode) => AssertFunction(_functions.GetDeclaredRoutine(index - 1), expectedHashCode);

        /// <summary>
        /// Checks the routine has expected hash code. The routine can be null.
        /// </summary>
        static bool AssertFunction(RoutineInfo routine, int expectedHashCode) => routine != null && routine.GetHashCode() == expectedHashCode;

        /// <summary>
        /// Gets declared function with given name. In case of more items they are considered as overloads.
        /// </summary>
        public RoutineInfo GetDeclaredFunction(string name) => _functions.GetDeclaredRoutine(name);

        /// <summary>Gets enumeration of all functions declared within the context, including library and user functions.</summary>
        /// <returns>Enumeration of all routines. Cannot be <c>null</c>.</returns>
        public IEnumerable<RoutineInfo> GetDeclaredFunctions() => _functions.EnumerateRoutines();

        /// <summary>
        /// Declare a runtime user type.
        /// </summary>
        /// <typeparam name="T">Type to be declared in current context.</typeparam>
        public void DeclareType<T>() => _types.DeclareType<T>();

        /// <summary>
        /// Declare a runtime user type unser an aliased name.
        /// </summary>
        /// <param name="tinfo">Original type descriptor.</param>
        /// <param name="typename">Type name alias, can differ from <see cref="PhpTypeInfo.Name"/>.</param>
        public void DeclareType(PhpTypeInfo tinfo, string typename) => _types.DeclareTypeAlias(tinfo, typename);

        /// <summary>
        /// Called by runtime when it expects that given type is declared.
        /// If not, autoload is invoked and if the type mismatches or cannot be declared, an exception is thrown.
        /// </summary>
        /// <typeparam name="T">Type which is expected to be declared.</typeparam>
        public void ExpectTypeDeclared<T>()
        {
            var tinfo = TypeInfoHolder<T>.TypeInfo;
            if (!_types.IsDeclared(tinfo))
            {
                // perform regular load with autoload
                if (GetDeclaredTypeOrThrow(tinfo.Name, true) != tinfo)
                {
                    throw new InvalidOperationException();
                }
            }
        }

        /// <summary>
        /// Gets runtime type information, or <c>null</c> if type with given is name not declared.
        /// </summary>
        public PhpTypeInfo GetDeclaredType(string name, bool autoload = false)
            => _types.GetDeclaredType(name) ?? (autoload ? this.AutoloadService.AutoloadTypeByName(name) : null);

        /// <summary>
        /// Gets runtime type information, or throws if type with given name is not declared.
        /// </summary>
        public PhpTypeInfo GetDeclaredTypeOrThrow(string name, bool autoload = false)
        {
            var tinfo = GetDeclaredType(name, autoload);
            if (tinfo == null)
            {
                PhpException.Throw(PhpError.Error, Resources.ErrResources.class_not_found, name);
            }

            return tinfo;
        }

        /// <summary>
        /// Gets runtime type information of given type by its name.
        /// Resolves reserved type names according to current caller context.
        /// Returns <c>null</c> if type was not resolved.
        /// </summary>
        public PhpTypeInfo ResolveType(string name, RuntimeTypeHandle callerCtx, bool autoload = false)
        {
            Debug.Assert(name != null);

            // reserved type names: parent, self, static
            if (name.Length == 6)
            {
                if (name.EqualsOrdinalIgnoreCase("parent"))
                {
                    if (!callerCtx.Equals(default(RuntimeTypeHandle)))
                    {
                        return Type.GetTypeFromHandle(callerCtx).GetPhpTypeInfo().BaseType;
                    }
                    return null;
                }
                else if (name.EqualsOrdinalIgnoreCase("static"))
                {
                    throw new NotImplementedException();
                }
            }
            else if (name.Length == 4 && name.EqualsOrdinalIgnoreCase("self"))
            {
                if (!callerCtx.Equals(default(RuntimeTypeHandle)))
                {
                    return Type.GetTypeFromHandle(callerCtx).GetPhpTypeInfo();
                }
                return null;
            }

            //
            return GetDeclaredType(name, autoload);
        }

        /// <summary>
        /// Gets enumeration of all types declared in current context.
        /// </summary>
        public IEnumerable<PhpTypeInfo> GetDeclaredTypes() => _types.GetDeclaredTypes();

        void FunctionRedeclared(RoutineInfo routine)
        {
            // TODO: ErrCode & throw
            throw new InvalidOperationException($"Function {routine.Name} redeclared!");
        }

        void TypeRedeclared(PhpTypeInfo type)
        {
            Debug.Assert(type != null);

            // TODO: ErrCode & throw
            throw new InvalidOperationException($"Type {type.Name} redeclared!");
        }

        #endregion

        #region Inclusions

        /// <summary>
        /// Used by runtime.
        /// Determines whether the <c>include_once</c> or <c>require_once</c> is allowed to proceed.
        /// </summary>
        public bool CheckIncludeOnce<TScript>() => !_scripts.IsIncluded<TScript>();

        /// <summary>
        /// Used by runtime.
        /// Called by scripts Main method at its begining.
        /// </summary>
        /// <typeparam name="TScript">Script type containing the Main method/</typeparam>
        public void OnInclude<TScript>() => _scripts.SetIncluded<TScript>();

        /// <summary>
        /// Resolves path according to PHP semantics, lookups the file in runtime tables and calls its Main method within the global scope.
        /// </summary>
        /// <param name="dir">Current script directory. Used for relative path resolution. Can be <c>null</c> to not resolve against current directory.</param>
        /// <param name="path">The relative or absolute path to resolve and include.</param>
        /// <param name="once">Whether to include according to include once semantics.</param>
        /// <param name="throwOnError">Whether to include according to require semantics.</param>
        /// <returns>Inclusion result value.</returns>
        public PhpValue Include(string dir, string path, bool once = false, bool throwOnError = false)
            => Include(dir, path, Globals,
                once: once,
                throwOnError: throwOnError);

        /// <summary>
        /// Resolves path according to PHP semantics, lookups the file in runtime tables and calls its Main method.
        /// </summary>
        /// <param name="cd">Current script directory. Used for relative path resolution. Can be <c>null</c> to not resolve against current directory.</param>
        /// <param name="path">The relative or absolute path to resolve and include.</param>
        /// <param name="locals">Variables scope for the included script.</param>
        /// <param name="this">Reference to <c>this</c> variable.</param>
        /// <param name="self">Reference to current class context.</param>
        /// <param name="once">Whether to include according to include once semantics.</param>
        /// <param name="throwOnError">Whether to include according to require semantics.</param>
        /// <returns>Inclusion result value.</returns>
        public PhpValue Include(string cd, string path, PhpArray locals, object @this = null, RuntimeTypeHandle self = default(RuntimeTypeHandle), bool once = false, bool throwOnError = false)
        {
            var script = ScriptsMap.ResolveInclude(path, RootPath, IncludePaths, WorkingDirectory, cd);
            if (script.IsValid)
            {
                if (once && _scripts.IsIncluded(script.Index))
                {
                    return PhpValue.Create(true);
                }
                else
                {
                    return script.Evaluate(this, locals, @this, self);
                }
            }
            else
            {
                if (TryIncludeFileContent(path))    // include non-compiled file (we do not allow dynamic compilation yet)
                {
                    return PhpValue.Null;
                }
                else
                {
                    var cause = string.Format(Resources.ErrResources.script_not_found, path);

                    PhpException.Throw(
                        throwOnError ? PhpError.Error : PhpError.Notice,
                        Resources.ErrResources.script_inclusion_failed, path, cause, string.Join(";", IncludePaths), cd);

                    if (throwOnError)
                    {
                        throw new ArgumentException(cause);
                    }

                    return PhpValue.False;
                }
            }
        }

        /// <summary>
        /// Tries to read a file and outputs its content.
        /// </summary>
        /// <param name="path">Path to the file. Will be resolved using available stream wrappers.</param>
        /// <returns><c>true</c> if file was read and outputted, otherwise <c>false</c>.</returns>
        bool TryIncludeFileContent(string path)
        {
            var fnc = this.GetDeclaredFunction("readfile");
            if (fnc != null)
            {
                return fnc.PhpCallable(this, (PhpValue)path).ToLong() >= 0;
            }
            else
            {
                return false;
            }
        }

        #endregion

        #region Constants

        /// <summary>
        /// Tries to get a global constant from current context.
        /// </summary>
        public bool TryGetConstant(string name, out PhpValue value)
        {
            int idx = 0;
            return TryGetConstant(name, out value, ref idx);
        }

        /// <summary>
        /// Tries to get a global constant from current context.
        /// </summary>
        internal bool TryGetConstant(string name, out PhpValue value, ref int idx)
        {
            value = _constants.GetConstant(name, ref idx);
            return value.IsSet;
        }

        /// <summary>
        /// Defines a runtime constant.
        /// </summary>
        public bool DefineConstant(string name, PhpValue value, bool ignorecase = false) => _constants.DefineConstant(name, value, ignorecase);

        /// <summary>
        /// Defines a runtime constant.
        /// </summary>
        internal bool DefineConstant(string name, PhpValue value, ref int idx, bool ignorecase = false) => _constants.DefineConstant(name, value, ref idx, ignorecase);

        /// <summary>
        /// Determines whether a constant with given name is defined.
        /// </summary>
        public bool IsConstantDefined(string name) => _constants.IsDefined(name);

        /// <summary>
        /// Gets enumeration of all available constants and their values.
        /// </summary>
        public IEnumerable<KeyValuePair<string, PhpValue>> GetConstants() => _constants;

        #endregion

        #region Shutdown

        List<Action<Context>> _lazyShutdownCallbacks = null;

        /// <summary>
        /// Enqueues a callback to be invoked at the end of request.
        /// </summary>
        /// <param name="action">Callback. Cannot be <c>null</c>.</param>
        public void RegisterShutdownCallback(Action<Context> action)
        {
            if (action == null)
            {
                throw new ArgumentNullException(nameof(action));
            }

            var callbacks = _lazyShutdownCallbacks;
            if (callbacks == null)
            {
                _lazyShutdownCallbacks = callbacks = new List<Action<Context>>(1);
            }

            callbacks.Add(action);
        }

        /// <summary>
        /// Invokes callbacks in <see cref="_lazyShutdownCallbacks"/> and disposes the list.
        /// </summary>
        void ProcessShutdownCallbacks()
        {
            var callbacks = _lazyShutdownCallbacks;
            if (callbacks != null)
            {
                for (int i = 0; i < callbacks.Count; i++)
                {
                    callbacks[i](this);
                }

                //
                _lazyShutdownCallbacks = callbacks = null;
            }
        }

        /// <summary>
        /// Closes current web session if opened.
        /// </summary>
        void ShutdownSessionHandler()
        {
            var webctx = HttpPhpContext;
            if (webctx != null && webctx.SessionState == PhpSessionState.Started)
            {
                webctx.SessionHandler.CloseSession(this, webctx, false);
            }
        }

        #endregion

        #region Resources // objects that need dispose

        HashSet<IDisposable> _lazyDisposables = null;

        public void RegisterDisposable(IDisposable obj)
        {
            if (_lazyDisposables == null)
            {
                _lazyDisposables = new HashSet<IDisposable>();
            }

            _lazyDisposables.Add(obj);
        }

        public void UnregisterDisposable(IDisposable obj)
        {
            if (_lazyDisposables != null)
            {
                _lazyDisposables.Remove(obj);
            }
        }

        void ProcessDisposables()
        {
            var set = _lazyDisposables;
            if (set != null && set.Count != 0)
            {
                _lazyDisposables = null;

                foreach (var x in set)
                {
                    x.Dispose();
                }
            }
        }

        #endregion

        #region Temporary Per-Request Files

        /// <summary>
        /// A list of temporary files which was created during the request and should be deleted at its end.
        /// </summary>
        private List<string>/*!*/TemporaryFiles
        {
            get
            {
                if (_temporaryFiles == null)
                    _temporaryFiles = new List<string>();

                return _temporaryFiles;
            }
        }
        private List<string> _temporaryFiles;

        /// <summary>
        /// Silently deletes all temporary files.
        /// </summary>
        private void DeleteTemporaryFiles()
        {
            if (_temporaryFiles != null)
            {
                for (int i = 0; i < _temporaryFiles.Count; i++)
                {
                    try
                    {
                        File.Delete(_temporaryFiles[i]);
                    }
                    catch { }
                }

                _temporaryFiles = null;
            }
        }

        /// <summary>
        /// Adds temporary file to current handler's temp files list.
        /// </summary>
        /// <param name="path">A path to the file.</param>
        protected void AddTemporaryFile(string path)
        {
            Debug.Assert(path != null);
            TemporaryFiles.Add(path);
        }

        /// <summary>
        /// Checks whether the given filename is a path to a temporary file
        /// (for example created using the filet upload mechanism).
        /// </summary>
        /// <remarks>
        /// The stored paths are checked case-insensitively.
        /// </remarks>
        /// <exception cref="ArgumentNullException">Argument is a <B>null</B> reference.</exception>
        public bool IsTemporaryFile(string path)
        {
            if (path == null)
            {
                throw new ArgumentNullException("path");
            }

            return _temporaryFiles != null && _temporaryFiles.Contains(path, CurrentPlatform.PathComparer);
        }

        /// <summary>
        /// Removes a file from a list of temporary files.
        /// </summary>
        /// <param name="path">A full path to the file.</param>
        /// <exception cref="ArgumentNullException">Argument is a <B>null</B> reference.</exception>
        public bool RemoveTemporaryFile(string path)
        {
            if (path == null)
            {
                throw new ArgumentNullException("path");
            }

            if (_temporaryFiles != null)
            {
                // NOTE: == List<T>.IndexOf(T, IEqualityComparer<T>)
                for (int i = 0; i < _temporaryFiles.Count; i++)
                {
                    if (CurrentPlatform.PathComparer.Compare(_temporaryFiles[i], path) == 0)
                    {
                        _temporaryFiles.RemoveAt(i);
                        return true;
                    }
                }
            }

            return false;
        }

        #endregion

        #region IDisposable

        bool _disposed;

        public virtual void Dispose()
        {
            if (!_disposed)
            {
                try
                {
                    ProcessShutdownCallbacks();
                    ProcessDisposables();
                    ShutdownSessionHandler();
                    //this.GuardedCall<object, object>(this.FinalizePhpObjects, null, false);
                    FinalizeBufferedOutput();

                    //// additional disposal action
                    //if (this.TryDispose != null)
                    //    this.TryDispose();
                }
                finally
                {
                    DeleteTemporaryFiles();

                    //// additional disposal action
                    //if (this.FinallyDispose != null)
                    //    this.FinallyDispose();

                    //
                    _disposed = true;
                }
            }
        }

        #endregion
    }
}
