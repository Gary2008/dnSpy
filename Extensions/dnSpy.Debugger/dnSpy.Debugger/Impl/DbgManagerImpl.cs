﻿/*
    Copyright (C) 2014-2017 de4dot@gmail.com

    This file is part of dnSpy

    dnSpy is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    dnSpy is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with dnSpy.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.Linq;
using dnSpy.Contracts.Debugger;
using dnSpy.Contracts.Debugger.Engine;

namespace dnSpy.Debugger.Impl {
	[Export(typeof(DbgManager))]
	sealed partial class DbgManagerImpl : DbgManager {
		public override DispatcherThread DispatcherThread => dispatcherThread;
		readonly DispatcherThreadImpl dispatcherThread;

		public override event EventHandler<DbgCollectionChangedEventArgs<DbgProcess>> ProcessesChanged;
		public override DbgProcess[] Processes {
			get {
				lock (lockObj)
					return processes.ToArray();
			}
		}
		readonly List<DbgProcessImpl> processes;

		public override event EventHandler IsDebuggingChanged;
		public override bool IsDebugging {
			get {
				lock (lockObj)
					return engines.Count > 0;
			}
		}

		public override event EventHandler IsRunningChanged;
		public override bool IsRunning {
			get {
				lock (lockObj)
					return cachedIsRunning;
			}
		}
		bool CalculateIsRunning_NoLock() {
			foreach (var info in engines) {
				// If at least one engine is paused, pretend all of them are
				if (info.EngineState == EngineState.Paused)
					return false;
			}
			return engines.Count != 0;
		}
		bool cachedIsRunning;

		public override event EventHandler<DbgCollectionChangedEventArgs<string>> DebugTagsChanged;
		public override string[] DebugTags {
			get {
				lock (lockObj)
					return debugTags.Tags;
			}
		}
		readonly TagsCollection debugTags;

		enum EngineState {
			/// <summary>
			/// Original state which it has before it gets connected (<see cref="DbgMessageConnected"/>)
			/// </summary>
			Starting,

			/// <summary>
			/// It's running
			/// </summary>
			Running,

			/// <summary>
			/// It's paused due to some pause event, eg. <see cref="DbgMessageBreak"/>
			/// </summary>
			Paused,
		}

		sealed class EngineInfo {
			public DbgEngine Engine { get; }
			public DbgProcessImpl Process { get; set; }
			public EngineState EngineState { get; set; } = EngineState.Starting;
			public string[] DebugTags { get; }
			public EngineInfo(DbgEngine engine) {
				Engine = engine;
				DebugTags = (string[])engine.DebugTags.Clone();
			}
		}

		readonly object lockObj;
		readonly List<EngineInfo> engines;
		readonly Lazy<DbgEngineProvider, IDbgEngineProviderMetadata>[] dbgEngineProviders;
		readonly Lazy<IDbgManagerStartListener, IDbgManagerStartListenerMetadata>[] dbgManagerStartListeners;
		int hasNotifiedStartListenersCounter;

		[ImportingConstructor]
		DbgManagerImpl([ImportMany] IEnumerable<Lazy<DbgEngineProvider, IDbgEngineProviderMetadata>> dbgEngineProviders, [ImportMany] IEnumerable<Lazy<IDbgManagerStartListener, IDbgManagerStartListenerMetadata>> dbgManagerStartListeners) {
			lockObj = new object();
			engines = new List<EngineInfo>();
			processes = new List<DbgProcessImpl>();
			debugTags = new TagsCollection();
			dispatcherThread = new DispatcherThreadImpl();
			this.dbgEngineProviders = dbgEngineProviders.OrderBy(a => a.Metadata.Order).ToArray();
			this.dbgManagerStartListeners = dbgManagerStartListeners.OrderBy(a => a.Metadata.Order).ToArray();
		}

		public override string Start(StartDebuggingOptions options) {
			lock (dbgManagerStartListeners) {
				if (hasNotifiedStartListenersCounter == 0) {
					hasNotifiedStartListenersCounter++;
					foreach (var lz in dbgManagerStartListeners)
						lz.Value.OnStart(this);
					hasNotifiedStartListenersCounter++;
				}
				else if (hasNotifiedStartListenersCounter != 2)
					throw new InvalidOperationException("Recursive call: Start()");
			}
			try {
				foreach (var lz in dbgEngineProviders) {
					var engine = lz.Value.Create(this, options);
					if (engine != null) {
						bool raiseIsDebuggingChanged, raiseIsRunningChanged;
						string[] addedDebugTags;
						lock (lockObj) {
							var oldIsRunning = cachedIsRunning;
							raiseIsDebuggingChanged = engines.Count == 0;
							var engineInfo = new EngineInfo(engine);
							engines.Add(engineInfo);
							addedDebugTags = debugTags.Add(engineInfo.DebugTags);
							cachedIsRunning = CalculateIsRunning_NoLock();
							raiseIsRunningChanged = oldIsRunning != cachedIsRunning;

							if (raiseIsDebuggingChanged || raiseIsRunningChanged || addedDebugTags.Length > 0) {
								DispatcherThread.BeginInvoke(() => {
									if (raiseIsDebuggingChanged)
										IsDebuggingChanged?.Invoke(this, EventArgs.Empty);
									if (raiseIsRunningChanged)
										IsRunningChanged?.Invoke(this, EventArgs.Empty);
									if (addedDebugTags.Length > 0)
										DebugTagsChanged?.Invoke(this, new DbgCollectionChangedEventArgs<string>(addedDebugTags, added: true));
								});
							}
						}
						engine.Message += DbgEngine_Message;
						engine.Start(options);
						return null;
					}
				}
			}
			catch (Exception ex) {
				return ex.ToString();
			}
			Debug.Fail("Couldn't create a debug engine");
			// Doesn't need to be localized, should be considered a bug if this is ever reached
			return "Couldn't create a debug engine";
		}

		bool IsOurEngine(DbgEngine engine) {
			lock (lockObj) {
				foreach (var info in engines) {
					if (info.Engine == engine)
						return true;
				}
			}
			return false;
		}

		void DbgEngine_Message(object sender, DbgEngineMessage e) {
			if (sender == null)
				throw new ArgumentNullException(nameof(sender));
			if (e == null)
				throw new ArgumentNullException(nameof(e));
			var engine = sender as DbgEngine;
			if (engine == null)
				throw new ArgumentOutOfRangeException(nameof(sender));

			if (!IsOurEngine(engine))
				return;

			switch (e.MessageKind) {
			case DbgEngineMessageKind.Connected:
				OnConnected(engine, (DbgMessageConnected)e);
				break;

			case DbgEngineMessageKind.Disconnected:
				OnDisconnected(engine, (DbgMessageDisconnected)e);
				break;

			case DbgEngineMessageKind.Break:
				OnBreak(engine, (DbgMessageBreak)e);
				break;

			default:
				//TODO:
				break;
			}
		}

		EngineInfo TryGetEngineInfo_NoLock(DbgEngine engine) {
			foreach (var info in engines) {
				if (info.Engine == engine)
					return info;
			}
			return null;
		}

		EngineInfo GetEngineInfo_NoLock(DbgEngine engine) {
			var info = TryGetEngineInfo_NoLock(engine);
			if (info == null)
				throw new InvalidOperationException("Unknown debug engine");
			return info;
		}

		DbgProcessImpl GetOrCreateProcess(int pid) {
			DbgProcessImpl process;
			lock (lockObj) {
				foreach (var p in processes) {
					if (p.Id == pid)
						return p;
				}
				process = new DbgProcessImpl(this, pid);
				processes.Add(process);
				DispatcherThread.BeginInvoke(() => ProcessesChanged?.Invoke(this, new DbgCollectionChangedEventArgs<DbgProcess>(process, added: true)));
			}
			return process;
		}

		void OnConnected(DbgEngine engine, DbgMessageConnected e) {
			if (e.ErrorMessage != null) {
				//TODO: Show error msg
				OnDisconnected(engine);
				return;
			}

			var process = GetOrCreateProcess(e.ProcessId);

			lock (lockObj) {
				var info = GetEngineInfo_NoLock(engine);
				info.Process = process;
				info.EngineState = EngineState.Running;
				breakAllHelper?.OnConnected_NoLock(info);
			}
			process.Add(engine, engine.CreateRuntime(process));
		}

		void OnDisconnected(DbgEngine engine, DbgMessageDisconnected e) => OnDisconnected(engine);

		void OnDisconnected(DbgEngine engine) {
			DbgProcess processToDispose = null;
			DbgRuntime runtime = null;
			bool raiseIsDebuggingChanged, raiseIsRunningChanged;
			string[] removedDebugTags;
			DbgProcessImpl process;
			lock (lockObj) {
				var oldIsRunning = cachedIsRunning;
				var info = TryGetEngineInfo_NoLock(engine);
				if (info != null)
					engines.Remove(info);
				process = info?.Process;
				raiseIsDebuggingChanged = engines.Count == 0;
				removedDebugTags = debugTags.Remove(info.DebugTags);
				cachedIsRunning = CalculateIsRunning_NoLock();
				raiseIsRunningChanged = oldIsRunning != cachedIsRunning;

				engine.Message -= DbgEngine_Message;
				if (process != null) {
					var pinfo = process.Remove(engine);
					runtime = pinfo.runtime;
					Debug.Assert(runtime != null);

					if (!pinfo.hasMoreRuntimes) {
						bool disposeProcess;
						disposeProcess = process.ExecuteLockedIfNoMoreRuntimes(() => processes.Remove(process), false);
						if (disposeProcess) {
							processToDispose = process;
							DispatcherThread.BeginInvoke(() => ProcessesChanged?.Invoke(this, new DbgCollectionChangedEventArgs<DbgProcess>(process, added: false)));
						}
					}
				}

				DispatcherThread.BeginInvoke(() => {
					engine.Close(DispatcherThread);
					runtime?.Close(DispatcherThread);
					processToDispose?.Close(DispatcherThread);
				});

				if (raiseIsDebuggingChanged || raiseIsRunningChanged || removedDebugTags.Length != 0) {
					DispatcherThread.BeginInvoke(() => {
						// Raise them in reverse order (see Start())
						if (removedDebugTags.Length != 0)
							DebugTagsChanged?.Invoke(this, new DbgCollectionChangedEventArgs<string>(removedDebugTags, added: false));
						if (raiseIsRunningChanged)
							IsRunningChanged?.Invoke(this, EventArgs.Empty);
						if (raiseIsDebuggingChanged)
							IsDebuggingChanged?.Invoke(this, EventArgs.Empty);
					});
				}

				breakAllHelper?.OnDisconnected_NoLock(engine);
			}
		}

		public override void BreakAll() {
			lock (lockObj) {
				if (breakAllHelper != null)
					return;
				breakAllHelper = new BreakAllHelper(this);
				breakAllHelper.Start_NoLock();
			}
		}
		BreakAllHelper breakAllHelper;

		void OnBreak(DbgEngine engine, DbgMessageBreak e) {
			lock (lockObj) {
				var info = TryGetEngineInfo_NoLock(engine);
				// It could've been disconnected
				if (info == null)
					return;

				if (e.ErrorMessage != null) {
					//TODO: Log the error
				}
				else
					info.EngineState = EngineState.Paused;
			}
			breakAllHelper?.OnBreak(engine);
		}

		void BreakCompleted(bool success) {
			lock (lockObj) {
				var oldIsRunning = cachedIsRunning;
				cachedIsRunning = CalculateIsRunning_NoLock();
				if (oldIsRunning != cachedIsRunning)
					DispatcherThread.BeginInvoke(() => IsRunningChanged?.Invoke(this, EventArgs.Empty));
			}
		}

		public override void RunAll() {
			lock (lockObj) {
				var oldIsRunning = cachedIsRunning;

				// If we're trying to break the processes, don't do a thing
				if (breakAllHelper != null)
					return;

				// Make a copy of it in the unlikely event that an engine gets disconnected
				// when we call Run() inside the lock
				foreach (var info in engines.ToArray()) {
					if (info.EngineState == EngineState.Paused) {
						info.EngineState = EngineState.Running;
						info.Engine.Run();
					}
				}

				cachedIsRunning = CalculateIsRunning_NoLock();
				if (oldIsRunning != cachedIsRunning)
					DispatcherThread.BeginInvoke(() => IsRunningChanged?.Invoke(this, EventArgs.Empty));
			}
		}

		public override void StopDebuggingAll() {
			lock (lockObj) {
				// Make a copy of it in the unlikely event that an engine gets disconnected
				// when we call StopDebugging() inside the lock
				foreach (var info in engines.ToArray())
					info.Engine.StopDebugging();
			}
		}

		public override void TerminateAll() {
			lock (lockObj) {
				// Make a copy of it in the unlikely event that an engine gets disconnected
				// when we call Terminate() inside the lock
				foreach (var info in engines.ToArray())
					info.Engine.Terminate();
			}
		}

		public override void DetachAll() {
			lock (lockObj) {
				// Make a copy of it in the unlikely event that an engine gets disconnected
				// when we call Detach() inside the lock
				foreach (var info in engines.ToArray())
					info.Engine.Detach();
			}
		}

		public override bool CanDetachWithoutTerminating {
			get {
				lock (lockObj) {
					foreach (var info in engines) {
						if (!info.Engine.CanDetach)
							return false;
					}
					return true;
				}
			}
		}
	}
}