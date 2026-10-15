using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace FcitxCjkInput {
    [StaticConstructorOnStartup]
    public static class FcitxCjkInputMod {
        private const string NativeLibraryName = "libfcitxcjkinput.so";
        private const string LogPath = "/tmp/fcitxcjkinput.log";
        private const int RtldNow = 2;
        private const int NativeBufferSize = 16384;
        private const int NativeDrainLimit = 32;
        private const int NativeRestartDelayMs = 2000;
        private const int LogBufferSize = 65536;
        private const long LogMaxBytes = 8L * 1024 * 1024;

        private static readonly long LogFlushInterval = Stopwatch.Frequency / 4L;

        private static readonly object LogLock = new object();
        private static readonly byte[] NativeBuffer = new byte[NativeBufferSize];
        private static readonly Dictionary<int, string> Engines = new Dictionary<int, string>();
        private static readonly Dictionary<int, ControlToken> ControlTokens =
            new Dictionary<int, ControlToken>();
        private static readonly Dictionary<ControlToken, string> ExpectedFieldTexts =
            new Dictionary<ControlToken, string>();
        private static readonly CompositionStateMachine Composition =
            new CompositionStateMachine(Stopwatch.Frequency * 2L);
        private static readonly CommittedCharacterTracker CommittedCharacters =
            new CommittedCharacterTracker();
        private static readonly GameplayKeyState GameplayKeys =
            new GameplayKeyState(Stopwatch.Frequency / 4L);
        private static readonly ShortcutCommitGuard ShortcutCommits =
            new ShortcutCommitGuard();
        private static readonly HashSet<uint> NativePressedKeys = new HashSet<uint>();
        private static readonly HashSet<uint> NativeConsumedKeys = new HashSet<uint>();
        private static readonly HashSet<uint> WaylithConsumedKeys = new HashSet<uint>();
        private static readonly List<WaylithRoutedKeyEvent> WaylithRoutedKeyEvents =
            new List<WaylithRoutedKeyEvent>();

        private static StreamWriter _log;
        private static IntPtr _nativeHandle;
        private static NativeSetDebug _nativeSetDebug;
        private static NativeStart _nativeStart;
        private static NativePoll _nativePoll;
        private static NativeStop _nativeStop;
        private static NativeSetFocus _nativeSetFocus;
        private static NativeProcessKey _nativeProcessKey;
        private static MethodInfo _controlIdForRect;
        private static MethodInfo _waylithShiftDown;
        private static MethodInfo _waylithCtrlDown;
        private static MethodInfo _waylithAltDown;
        private static MethodInfo _waylithMetaDown;
        private static string _engine = "unknown";
        private static bool _nativeReady;
        private static bool _nativeDirectInputAvailable;
        private static bool? _nativeFocusState;
        private static long _nativeFocusRetryAt;
        private static bool _nativeLoaded;
        private static int _mainThreadId;
        private static int _nativePollFrame = -1;
        private static long _restartAt;
        private static int _focusedControl;
        private static int _desktopTextFieldHandledDepth;
        private static int _focusedTextFieldFrame = -10;
        private static int _lastZContext;
        private static int _unboundPreeditContext;
        private static string _unboundPreedit = "";
        private static long _focusGeneration;
        private static bool _logInitialized;
        private static bool _textFieldActive;
        private static bool _searchWidgetFocused;
        private static QuickSearchFilter _quickSearchFilter;
        private static ControlToken _quickSearchTarget;
        private static string _quickSearchCommittedText = "";
        private static string _quickSearchRenderedText = "";
        private static bool _settingQuickSearchText;
        private static bool _quickSearchCaretSyncPending;
        private static int _quickSearchPendingCaret = -1;
        private static IMECompositionMode _imeModeWithoutTextField = IMECompositionMode.Auto;
        private static int _quickSearchFocusFrame = -1;
        private static int _shuttingDown;
        private static long _lastLogFlush;

        private static bool DebugLogging => FcitxCjkInputEntry.Settings?.DebugLog == true;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void NativeSetDebug(int enabled);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int NativeStart(uint pid);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int NativePoll([Out] byte[] buffer, int capacity);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void NativeStop();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int NativeSetFocus(int focused);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int NativeProcessKey(uint keyval, uint keycode, uint state, int release,
            uint time);

        private struct WaylithRoutedKeyEvent {
            public int Frame;
            public uint Keyval;
            public bool Release;
            public bool Accepted;
            public bool Modifier;
        }

        [DllImport("libdl.so.2")]
        private static extern IntPtr dlopen(string fileName, int flags);

        [DllImport("libdl.so.2")]
        private static extern IntPtr dlsym(IntPtr handle, string symbol);

        [DllImport("libdl.so.2")]
        private static extern IntPtr dlerror();

        static FcitxCjkInputMod() {
            if (Application.platform != RuntimePlatform.LinuxPlayer)
                return;

            try {
                _mainThreadId = Thread.CurrentThread.ManagedThreadId;
                _imeModeWithoutTextField = Input.imeCompositionMode;
                EnsureLog();
                WriteRuntimeHeader("INIT");
                LoadNativeBridge();
                Application.quitting += Shutdown;
                _nativeSetDebug(DebugLogging ? 1 : 0);
                Patch();
                StartNativeBridge();
                Log.Message("[CJK] fcitx5 SDL/IMGUI bridge initialized; " +
                    (DebugLogging ? "log=" + LogPath : "debug log disabled"));
            } catch (Exception exception) {
                WriteLog("FATAL " + exception);
                Shutdown();
                Log.Error("[CJK] initialization failed: " + exception);
            }
        }

        internal static void SetDebugLogging(bool enabled) {
            if (Volatile.Read(ref _shuttingDown) != 0)
                return;
            if (enabled)
                EnsureLog();
            if (_nativeSetDebug != null)
                _nativeSetDebug(enabled ? 1 : 0);
            if (enabled) {
                WriteRuntimeHeader("DEBUG enabled");
                Log.Message("[CJK] debug log enabled; log=" + LogPath);
            } else {
                ExpectedFieldTexts.Clear();
                lock (LogLock) {
                    _log?.Dispose();
                    _log = null;
                }
                Log.Message("[CJK] debug log disabled");
            }
        }

        private static void WriteRuntimeHeader(string reason) {
            WriteLog(reason + " unity=" + Application.unityVersion + " pid=" +
                Process.GetCurrentProcess().Id + " XMODIFIERS=" +
                Environment.GetEnvironmentVariable("XMODIFIERS") + " SDL_IM_MODULE=" +
                Environment.GetEnvironmentVariable("SDL_IM_MODULE") + " engine=" + _engine +
                " native=" + _nativeReady + " ime=" + Input.imeCompositionMode);
        }

        private static void Patch() {
            var harmony = new Harmony("scarf.cjkinput");
            var rootOnGui = AccessTools.Method(typeof(Root), "OnGUI");
            //var desktopTextField = AccessTools.Method(typeof(GUI), "HandleTextFieldEventForDesktop");
            var desktopTextField = AccessTools.Method(typeof(GUI), "DoTextField", new[] {
                typeof(Rect), typeof(int), typeof(GUIContent), typeof(bool), typeof(int),
                typeof(GUIStyle)
            });
            var textFieldImpl = AccessTools.Method(typeof(GUI), "TextFieldImpl", new[] {
                typeof(Rect), typeof(string), typeof(GUIStyle), typeof(bool)
            });
            _controlIdForRect = AccessTools.Method(typeof(GUI), "ControlIdFor",
                new[] { typeof(Rect) });
            var drawTextAreaContent = AccessTools.Method(typeof(GUI), "DrawTextAreaContent");
            var quickSearch = AccessTools.Method(typeof(QuickSearchWidget), nameof(QuickSearchWidget.OnGUI));
            var searchTextSetter = AccessTools.PropertySetter(typeof(QuickSearchFilter),
                nameof(QuickSearchFilter.Text));
            var keyBindingIsDown = AccessTools.PropertyGetter(typeof(KeyBindingDef),
                nameof(KeyBindingDef.IsDown));
            var keyBindingKeyDownEvent = AccessTools.PropertyGetter(typeof(KeyBindingDef),
                nameof(KeyBindingDef.KeyDownEvent));
            var playSettingsControls = AccessTools.Method(typeof(PlaySettings),
                nameof(PlaySettings.DoPlaySettingsGlobalControls));
            if (rootOnGui == null)
                throw new MissingMethodException("Verse.Root.OnGUI");
            //if (desktopTextField == null)
            //    throw new MissingMethodException("UnityEngine.GUI.HandleTextFieldEventForDesktop");
            if (desktopTextField == null)
                throw new MissingMethodException("UnityEngine.GUI.DoTextField(Rect, Int32, GUIContent, Boolean, Int32, GUIStyle)");
            if (textFieldImpl == null)
                throw new MissingMethodException("UnityEngine.GUI.TextFieldImpl(Rect, String, GUIStyle, Boolean)");
            if (_controlIdForRect == null)
                throw new MissingMethodException("UnityEngine.GUI.ControlIdFor(Rect)");
            if (drawTextAreaContent == null)
                throw new MissingMethodException("UnityEngine.GUI.DrawTextAreaContent");
            if (quickSearch == null)
                throw new MissingMethodException("RimWorld.QuickSearchWidget.OnGUI");
            if (searchTextSetter == null)
                throw new MissingMethodException("RimWorld.QuickSearchFilter.Text.set");
            if (keyBindingIsDown == null)
                throw new MissingMethodException("Verse.KeyBindingDef.IsDown.get");
            if (keyBindingKeyDownEvent == null)
                throw new MissingMethodException("Verse.KeyBindingDef.KeyDownEvent.get");
            if (playSettingsControls == null)
                throw new MissingMethodException(
                    "RimWorld.PlaySettings.DoPlaySettingsGlobalControls");

            harmony.Patch(rootOnGui,
                prefix: new HarmonyMethod(typeof(FcitxCjkInputMod), nameof(BeforeRootOnGui)));

            var waylithInputState = AccessTools.TypeByName("Waylith.Input.InputState");
            var waylithOnKey = waylithInputState?.GetMethods(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .FirstOrDefault(method => {
                    if (method.Name != "OnKey")
                        return false;
                    var parameters = method.GetParameters();
                    return parameters.Length == 2 && parameters[0].ParameterType.IsEnum &&
                        parameters[1].ParameterType == typeof(bool);
                });
            if (waylithOnKey != null) {
                _waylithShiftDown = AccessTools.PropertyGetter(waylithInputState, "ShiftDown");
                _waylithCtrlDown = AccessTools.PropertyGetter(waylithInputState, "CtrlDown");
                _waylithAltDown = AccessTools.PropertyGetter(waylithInputState, "AltDown");
                _waylithMetaDown = AccessTools.PropertyGetter(waylithInputState, "MetaDown");
                harmony.Patch(waylithOnKey,
                    prefix: new HarmonyMethod(typeof(FcitxCjkInputMod),
                        nameof(BeforeWaylithInputKey)));
            }
            harmony.Patch(desktopTextField,
                prefix: new HarmonyMethod(typeof(FcitxCjkInputMod), nameof(BeforeDesktopTextField)),
                postfix: new HarmonyMethod(typeof(FcitxCjkInputMod), nameof(AfterDesktopTextField)));
            harmony.Patch(textFieldImpl,
                prefix: new HarmonyMethod(typeof(FcitxCjkInputMod), nameof(BeforeTextFieldImpl)),
                postfix: new HarmonyMethod(typeof(FcitxCjkInputMod), nameof(AfterTextFieldImpl)));
            harmony.Patch(drawTextAreaContent,
                prefix: new HarmonyMethod(typeof(FcitxCjkInputMod), nameof(BeforeDrawTextAreaContent)));
            harmony.Patch(quickSearch,
                prefix: new HarmonyMethod(typeof(FcitxCjkInputMod), nameof(BeforeQuickSearch)),
                postfix: new HarmonyMethod(typeof(FcitxCjkInputMod), nameof(AfterQuickSearch)));
            harmony.Patch(searchTextSetter,
                prefix: new HarmonyMethod(typeof(FcitxCjkInputMod), nameof(BeforeSearchTextSet)));
            harmony.Patch(keyBindingIsDown,
                postfix: new HarmonyMethod(typeof(FcitxCjkInputMod), nameof(AfterKeyBindingIsDown)));
            harmony.Patch(keyBindingKeyDownEvent,
                postfix: new HarmonyMethod(typeof(FcitxCjkInputMod),
                    nameof(AfterKeyBindingKeyDownEvent)));
            harmony.Patch(playSettingsControls,
                postfix: new HarmonyMethod(typeof(FcitxCjkInputMod),
                    nameof(AfterPlaySettingsControls)));
            WriteLog("PATCH Root.OnGUI=" + rootOnGui + " textField=" + desktopTextField +
                " textFieldImpl=" + textFieldImpl +
                " waylithInputKey=" + (waylithOnKey?.ToString() ?? "unavailable") +
                " quickSearch=" + quickSearch + " searchTextSetter=" + searchTextSetter +
                " keyBindingIsDown=" + keyBindingIsDown + " keyBindingKeyDownEvent=" +
                keyBindingKeyDownEvent + " playSettingsControls=" + playSettingsControls);
        }

        private static void LoadNativeBridge() {
            var assembly = typeof(FcitxCjkInputMod).Assembly;
            var content = LoadedModManager.RunningModsListForReading
                .FirstOrDefault(mod => mod.assemblies.loadedAssemblies.Contains(assembly));
            var assemblyDirectory = content != null
                ? Path.Combine(content.RootDir, "1.7", "Assemblies")
                : Path.GetDirectoryName(typeof(FcitxCjkInputMod).Assembly.Location);
            var path = Path.Combine(assemblyDirectory, NativeLibraryName);
            _nativeHandle = dlopen(path, RtldNow);
            if (_nativeHandle == IntPtr.Zero)
                throw new DllNotFoundException(path + ": " + GetDlError());

            _nativeSetDebug = LoadNativeFunction<NativeSetDebug>("fcitx_bridge_set_debug");
            _nativeStart = LoadNativeFunction<NativeStart>("fcitx_bridge_start");
            _nativePoll = LoadNativeFunction<NativePoll>("fcitx_bridge_poll");
            _nativeStop = LoadNativeFunction<NativeStop>("fcitx_bridge_stop");
            _nativeSetFocus = TryLoadNativeFunction<NativeSetFocus>("fcitx_bridge_set_focus");
            _nativeProcessKey = TryLoadNativeFunction<NativeProcessKey>("fcitx_bridge_process_key");
            _nativeDirectInputAvailable = _nativeSetFocus != null && _nativeProcessKey != null;
            _nativeLoaded = true;
            WriteLog("NATIVE loaded path=" + path);
            if (!_nativeDirectInputAvailable)
                WriteLog("NATIVE direct input exports missing; rebuild libfcitxcjkinput.so");
        }

        private static T TryLoadNativeFunction<T>(string name) where T : class {
            var pointer = dlsym(_nativeHandle, name);
            return pointer == IntPtr.Zero
                ? null
                : (T)(object)Marshal.GetDelegateForFunctionPointer(pointer, typeof(T));
        }

        private static T LoadNativeFunction<T>(string name) where T : class {
            var pointer = dlsym(_nativeHandle, name);
            if (pointer == IntPtr.Zero)
                throw new MissingMethodException(name + ": " + GetDlError());
            return (T)(object)Marshal.GetDelegateForFunctionPointer(pointer, typeof(T));
        }

        private static string GetDlError() {
            var pointer = dlerror();
            return pointer == IntPtr.Zero ? "unknown dlerror" : Marshal.PtrToStringAnsi(pointer);
        }

        private static void StartNativeBridge() {
            if (Volatile.Read(ref _shuttingDown) != 0)
                return;
            _restartAt = 0;
            _nativeReady = false;
            _nativeFocusState = null;
            NativePressedKeys.Clear();
            NativeConsumedKeys.Clear();
            WaylithConsumedKeys.Clear();
            WaylithRoutedKeyEvents.Clear();
            var result = _nativeStart((uint)Process.GetCurrentProcess().Id);
            WriteLog("NATIVE start result=" + result);
            if (result != 0)
                ScheduleNativeRestart();
        }

        private static void ScheduleNativeRestart() {
            if (Volatile.Read(ref _shuttingDown) != 0)
                return;
            _restartAt = Stopwatch.GetTimestamp() +
                Stopwatch.Frequency * NativeRestartDelayMs / 1000L;
        }

        private static void DrainNativeMessages() {
            if (Volatile.Read(ref _shuttingDown) != 0)
                return;
            if (Thread.CurrentThread.ManagedThreadId != _mainThreadId)
                return;
            if (!_nativeLoaded)
                return;

            for (var count = 0; count < NativeDrainLimit; count++) {
                var length = _nativePoll(NativeBuffer, NativeBuffer.Length);
                if (length <= 0)
                    break;
                var line = Encoding.UTF8.GetString(NativeBuffer, 0, length);
                try {
                    HandleNativeMessage(line);
                } catch (Exception exception) {
                    WriteLog("RX error line=[" + Escape(line) + "] exception=" + exception);
                }
            }
        }

        private static void HandleNativeMessage(string line) {
            if (line.StartsWith("LOG:", StringComparison.Ordinal)) {
                WriteLog("NATIVE " + line.Substring(4));
                return;
            }
            if (line.StartsWith("ERROR:", StringComparison.Ordinal)) {
                WriteLog("NATIVE " + line);
                return;
            }

            if (DebugLogging)
                WriteLog("RX " + line);
            if (line.StartsWith("READY:", StringComparison.Ordinal)) {
                _nativeReady = true;
                _nativeFocusState = null;
                _restartAt = 0;
                return;
            }
            if (line == "STOPPED") {
                _nativeReady = false;
                _nativeFocusState = null;
                NativePressedKeys.Clear();
                NativeConsumedKeys.Clear();
                WaylithConsumedKeys.Clear();
                WaylithRoutedKeyEvents.Clear();
                Engines.Clear();
                Composition.Reset();
                CommittedCharacters.Clear();
                GameplayKeys.Clear();
                SetEngine("unknown");
                ScheduleNativeRestart();
                return;
            }
            if (!line.StartsWith("EVENT:", StringComparison.Ordinal)) {
                WriteLog("RX unknown line=[" + Escape(line) + "]");
                return;
            }

            var parts = line.Split(new[] { ':' }, 5);
            if (parts.Length != 5 || !int.TryParse(parts[1], out var contextId) ||
                !long.TryParse(parts[2], out var sequence))
                throw new FormatException("Invalid native event: " + line);
            HandleContextEvent(contextId, sequence, parts[3], parts[4]);
        }

        private static void HandleContextEvent(int contextId, long sequence, string kind, string payload) {
            var now = Stopwatch.GetTimestamp();
            if (kind == "RESET") {
                Engines.Clear();
                Composition.ResetAndDiscardActions();
                CommittedCharacters.Clear();
                GameplayKeys.Clear();
                ClearShortcutComposition(0);
                SetEngine("unknown");
                _nativeStop?.Invoke();
                StartNativeBridge();
                return;
            }
            if (kind == "HANGUL_PREEDIT" || kind == "HANGUL_COMMIT") {
                SetHangulEngine(contextId);
                kind = kind == "HANGUL_PREEDIT" ? "PREEDIT" : "COMMIT";
            }
            if (kind == "ENGINE") {
                Engines[contextId] = payload;
                if (payload != "hangul") {
                    Composition.CancelComposition(contextId);
                    GameplayKeys.Clear();
                    ClearShortcutComposition(contextId);
                }
                if (Composition.ActiveContext == 0 || Composition.ActiveContext == contextId)
                    SetEngine(payload);
                return;
            }
            if (kind == "FOCUS") {
                if (payload == "IN") {
                    Composition.FocusIn(contextId, sequence);
                    SetEngine(Engines.TryGetValue(contextId, out var engine) ? engine : "unknown");
                } else if (payload == "OUT") {
                    GameplayKeys.Clear();
                    ClearShortcutComposition(contextId);
                    if (Composition.FocusOut(contextId, sequence))
                        SetEngine("unknown");
                }
                return;
            }
            if (kind == "KEY") {
                var keyParts = payload.Split(':');
                if (keyParts.Length != 3)
                    throw new FormatException("Invalid key event: " + payload);
                var keyValue = int.Parse(keyParts[0]);
                var release = int.Parse(keyParts[1]) != 0;
                var observedAt = long.Parse(keyParts[2]) * Stopwatch.Frequency / 1000L;
                SetHangulEngine(contextId);
                if (!release && (keyValue == 'z' || keyValue == 'Z'))
                    _lastZContext = contextId;
                var gameplayBlocked = _textFieldActive || IsSearchWidgetFocused();
                if (release || !gameplayBlocked)
                    GameplayKeys.Update(keyValue, release, observedAt, now);
                return;
            }
            if (kind == "KEYRESET") {
                GameplayKeys.Clear();
                ClearShortcutComposition(0);
                return;
            }
            if (kind == "PREEDIT") {
                var separator = payload.IndexOf(':');
                if (separator < 0)
                    throw new FormatException("Missing preedit cursor separator: " + payload);
                var cursorBytes = int.Parse(payload.Substring(0, separator));
                var bytes = DecodeHexBytes(payload.Substring(separator + 1));
                var clampedCursorBytes = Math.Max(0, Math.Min(cursorBytes, bytes.Length));
                var text = Encoding.UTF8.GetString(bytes);
                var cursor = Encoding.UTF8.GetString(bytes, 0, clampedCursorBytes).Length;
                if (text.Length == 0)
                    ClearShortcutComposition(contextId);
                if (Composition.Preedit(contextId, sequence, text, cursor)) {
                    _unboundPreeditContext = 0;
                    _unboundPreedit = "";
                    if (Engines.TryGetValue(contextId, out var engine))
                        SetEngine(engine);
                    if (DebugLogging)
                        WriteLog("STATE preedit context=" + contextId + " control=" +
                            _focusedControl + " cursorBytes=" + cursorBytes + " cursorChars=" +
                            cursor + " text=[" + Escape(text) + "]");
                } else {
                    _unboundPreeditContext = contextId;
                    _unboundPreedit = text;
                    if (DebugLogging)
                        WriteLog("DROP preedit context=" + contextId + " sequence=" + sequence +
                            " reason=inactive-or-unbound");
                }
                return;
            }
            if (kind == "COMMIT") {
                var text = DecodeHex(payload);
                if (ShortcutCommits.ShouldDiscard(contextId, text)) {
                    if (DebugLogging)
                        WriteLog("DROP shortcut commit context=" + contextId + " sequence=" +
                            sequence + " text=[" + Escape(text) + "]");
                    return;
                }
                Engines.TryGetValue(contextId, out var engine);
                if (ImeRouting.IsImeEngine(engine) &&
                    (engine == "hangul" || ContainsNonAscii(text)) && text.Length > 0 &&
                    Composition.Commit(contextId, sequence, text, now)) {
                    if (DebugLogging)
                        WriteLog("QUEUE commit context=" + contextId + " sequence=" + sequence +
                            " text=[" + Escape(text) + "] count=" + Composition.PendingCount);
                } else if (DebugLogging) {
                    WriteLog("DROP commit context=" + contextId + " sequence=" + sequence +
                        " engine=" + (engine ?? "unknown") + " text=[" + Escape(text) + "]");
                }
                return;
            }
            WriteLog("RX unknown event kind=" + kind + " payload=[" + Escape(payload) + "]");
        }

        private static void ClearShortcutComposition(int contextId) {
            ShortcutCommits.Cancel(contextId);
            if (contextId == 0 || _lastZContext == contextId)
                _lastZContext = 0;
            if (contextId == 0 || _unboundPreeditContext == contextId) {
                _unboundPreeditContext = 0;
                _unboundPreedit = "";
            }
        }

        private static void SetHangulEngine(int contextId) {
            Engines[contextId] = "hangul";
            if (Composition.ActiveContext == 0 || Composition.ActiveContext == contextId)
                SetEngine("hangul");
        }

        private static void SetEngine(string engine) {
            if (DebugLogging && _engine != engine)
                WriteLog("STATE engine " + _engine + " -> " + engine);
            _engine = engine;
        }

        private static bool IsSearchWidgetFocused() {
            if (_quickSearchFocusFrame >= 0 &&
                Time.frameCount - _quickSearchFocusFrame <= 1)
                return true;
            // Find.WindowStack is backed by the active game and may not be
            // available while Root.OnGUI is drawing the startup/menu screen.
            if (Current.Game == null)
                return false;
            var windowStack = Find.WindowStack;
            return windowStack != null && windowStack.AnySearchWidgetFocused;
        }

        private static void BeforeRootOnGui() {
            var now = Stopwatch.GetTimestamp();
            WaylithRoutedKeyEvents.RemoveAll(keyEvent =>
                Time.frameCount - keyEvent.Frame > 3);
            if (_restartAt != 0 && now >= _restartAt)
                StartNativeBridge();
            Composition.DiscardExpired(now);
            GameplayKeys.DiscardExpired(now);
            if (GUIUtility.keyboardControl == 0 && _focusedControl != 0) {
                _focusedControl = 0;
                Composition.Blur();
            }
            var searchWidgetFocused = IsSearchWidgetFocused();
            if (searchWidgetFocused != _searchWidgetFocused) {
                _searchWidgetFocused = searchWidgetFocused;
                if (DebugLogging)
                    WriteLog("STATE search-focus=" + searchWidgetFocused + " frame=" +
                        Time.frameCount + " keyboardControl=" + GUIUtility.keyboardControl);
            }
            var textFieldActive = ImeRouting.TextFieldIsActive(GUIUtility.keyboardControl,
                _focusedControl, _focusedTextFieldFrame, Time.frameCount, searchWidgetFocused);
            _textFieldActive = textFieldActive;
            if (_nativePollFrame != Time.frameCount) {
                _nativePollFrame = Time.frameCount;
                DrainNativeMessages();
            }
            UpdateNativeFocus(textFieldActive);
            SetImeCompositionMode(textFieldActive, "root");

            var currentEvent = Event.current;
            if (currentEvent == null)
                return;
            LogRootEvent(currentEvent, textFieldActive);
        }

        private static bool BeforeWaylithInputKey(object __instance, object[] __args) {
            if (__args == null || __args.Length < 2 || __args[0] == null ||
                !(__args[1] is bool down))
                return true;

            var keyName = __args[0].ToString();
            if (!_nativeDirectInputAvailable || !_nativeReady) {
                if (DebugLogging)
                    WriteLog("FCITX waylith-key skip=bridge-unavailable key=" + keyName +
                        " ready=" + _nativeReady + " direct=" + _nativeDirectInputAvailable);
                return true;
            }
            if (!_textFieldActive) {
                if (DebugLogging)
                    WriteLog("FCITX waylith-key skip=no-focused-textfield key=" + keyName + " " +
                        DescribeEvent());
                return true;
            }
            if (string.IsNullOrEmpty(keyName) || !TryGetFcitxKey(keyName, out var keyval)) {
                if (DebugLogging)
                    WriteLog("FCITX waylith-key skip=unmapped key=" + keyName + " " +
                        DescribeEvent());
                return true;
            }

            UpdateNativeFocus(true);
            var state = WaylithModifierState(__instance);
            var modifier = IsModifierKeysym(keyval);
            var release = !down;
            var accepted = false;

            if (down) {
                var keyDownState = RemoveModifierSelfState(keyval, state);
                accepted = SendNativeKey(keyval, keyDownState, release: false);
                NativePressedKeys.Add(keyval);
                if (accepted)
                    NativeConsumedKeys.Add(keyval);
            } else if (NativePressedKeys.Remove(keyval)) {
                accepted = SendNativeKey(keyval, state, release: true);
                var consumed = NativeConsumedKeys.Remove(keyval);
                if (consumed && !modifier)
                    WaylithConsumedKeys.Add(keyval);
            }

            WaylithRoutedKeyEvents.Add(new WaylithRoutedKeyEvent {
                Frame = Time.frameCount,
                Keyval = keyval,
                Release = release,
                Accepted = accepted,
                Modifier = modifier
            });

            if (DebugLogging)
                WriteLog("FCITX key source=waylith-input-state key=" + keyName +
                    " keysym=0x" + keyval.ToString("X") + " state=0x" + state.ToString("X") +
                    " release=" + release + " accepted=" + accepted + " " +
                    DescribeEvent());

            // Suppress a consumed non-modifier press and its matching release before Waylith
            // creates Unity events. Modifier events must reach InputState so its modifier
            // bookkeeping remains balanced.
            if (down && accepted && !modifier)
                return false;
            if (release && !modifier && WaylithConsumedKeys.Remove(keyval))
                return false;
            return true;
        }

        private static uint WaylithModifierState(object inputState) {
            var state = 0u;
            if (ReadWaylithModifier(inputState, _waylithShiftDown, EventModifiers.Shift))
                state |= 1u << 0;
            if (ReadWaylithModifier(inputState, _waylithCtrlDown, EventModifiers.Control))
                state |= 1u << 2;
            if (ReadWaylithModifier(inputState, _waylithAltDown, EventModifiers.Alt))
                state |= 1u << 3;
            if (ReadWaylithModifier(inputState, _waylithMetaDown, EventModifiers.Command))
                state |= 1u << 6;
            if (Event.current != null &&
                (Event.current.modifiers & EventModifiers.CapsLock) != 0)
                state |= 1u << 1;
            return state;
        }

        private static bool ReadWaylithModifier(object inputState, MethodInfo getter,
            EventModifiers fallback) {
            if (inputState != null && getter != null) {
                try {
                    return (bool)getter.Invoke(inputState, null);
                } catch {
                    // The Unity event state below is a safe fallback if Waylith changes its API.
                }
            }
            return Event.current != null && (Event.current.modifiers & fallback) != 0;
        }

        private static void BeforeDesktopTextField(Rect position, int id, GUIContent content,
            bool multiline, int maxLength, GUIStyle style, out bool __state) {
            __state = id != 0 && GUIUtility.keyboardControl == id;
            if (!__state) {
                var focusEvent = Event.current;
                if (DebugLogging && focusEvent != null &&
                    (focusEvent.rawType == EventType.KeyDown ||
                        focusEvent.rawType == EventType.KeyUp))
                    WriteLog("FIELD skip=not-focused id=" + id + " keyboardControl=" +
                        GUIUtility.keyboardControl + " multiline=" + multiline + " " +
                        DescribeEvent());
                return;
            }
            _desktopTextFieldHandledDepth++;

            var editor = (TextEditor)GUIUtility.GetStateObject(typeof(TextEditor), id);
            var target = ObserveEditor(id, editor);
            LogTextField("FIELD before-original", target, id, content, editor);
            if (target.Id == 0)
                return;
            UpdateNativeFocus(true);
            var currentEvent = Event.current;
            if (!IsSearchWidgetFocused())
                ProcessNativeKeyEvent(currentEvent, "text-field");
            SuppressRawHangulKey(id);
            if (!IsSearchWidgetFocused() && currentEvent != null &&
                ProcessNativeTextCharacter(currentEvent, "text-field"))
                currentEvent.Use();
            if (DebugLogging)
                VerifyCommittedText(target, id, content, editor);

            var actions = Composition.TakeActions(target, Stopwatch.GetTimestamp());
            if (actions.Count > 0) {
                var inserted = DebugLogging ? new StringBuilder() : null;
                foreach (var action in actions) {
                    var insertedLength = ApplyAction(editor, maxLength, action, inserted);
                    CommittedCharacters.Expect(target, action.Text, insertedLength, Time.frameCount);
                }
                content.text = editor.text;
                if (DebugLogging)
                    ExpectedFieldTexts[target] = editor.text;
                GUI.changed = true;
                if (DebugLogging)
                    WriteLog("INSERT event=" + Event.current.type + " control=" + id + " text=[" +
                        Escape(inserted.ToString()) + "] result=[" + Escape(editor.text) + "] cursor=" +
                        editor.cursorIndex + " select=" + editor.selectIndex + " pending=" +
                        Composition.PendingCount);
                if (GUIUtility.keyboardControl == id)
                    Composition.Focus(target, editor.cursorIndex, editor.selectIndex);
            }
            SuppressCommittedCharacter(target, id);
        }

        private static void AfterDesktopTextField(Rect position, int id, GUIContent content,
            bool multiline, int maxLength, GUIStyle style, bool __state) {
            if (__state)
                _desktopTextFieldHandledDepth = Math.Max(0,
                    _desktopTextFieldHandledDepth - 1);
            if (GUIUtility.keyboardControl != id)
                return;

            var editor = (TextEditor)GUIUtility.GetStateObject(typeof(TextEditor), id);
            var target = ObserveEditor(id, editor);
            LogTextField("FIELD after-original", target, id, content, editor);

        }

        private static void BeforeTextFieldImpl(Rect rect, ref string text, GUIStyle style,
            bool multiline) {
            if (_desktopTextFieldHandledDepth > 0 || IsSearchWidgetFocused() ||
                !TryGetFocusedTextEditor(rect, out var id, out var editor))
                return;

            text = text ?? "";
            if (editor.text != text) {
                editor.text = text;
                editor.cursorIndex = Math.Max(0, Math.Min(editor.cursorIndex, text.Length));
                editor.selectIndex = Math.Max(0, Math.Min(editor.selectIndex, text.Length));
            }

            var target = ObserveEditor(id, editor);
            if (target.Id == 0)
                return;
            UpdateNativeFocus(true);

            var currentEvent = Event.current;
            ProcessNativeKeyEvent(currentEvent, "text-field-impl");
            SuppressRawHangulKey(id);
            if (currentEvent != null && !IsSearchWidgetFocused() &&
                ProcessNativeTextCharacter(currentEvent, "text-field-impl"))
                currentEvent.Use();

            var actions = Composition.TakeActions(target, Stopwatch.GetTimestamp());
            if (actions.Count > 0) {
                var inserted = DebugLogging ? new StringBuilder() : null;
                foreach (var action in actions) {
                    var insertedLength = ApplyAction(editor, int.MaxValue, action, inserted);
                    CommittedCharacters.Expect(target, action.Text, insertedLength,
                        Time.frameCount);
                }
                text = editor.text;
                GUI.changed = true;
                Composition.Focus(target, editor.cursorIndex, editor.selectIndex);
                if (DebugLogging)
                    WriteLog("INSERT text-field-impl control=" + id + " text=[" +
                        Escape(inserted.ToString()) + "] result=[" + Escape(editor.text) +
                        "] cursor=" + editor.cursorIndex + " select=" + editor.selectIndex +
                        " pending=" + Composition.PendingCount);
            }
            SuppressCommittedCharacter(target, id);
            LogTextEditor("FIELD impl-before", target, id, text, editor, multiline);
        }

        private static void AfterTextFieldImpl(Rect rect, string text, GUIStyle style,
            bool multiline, string __result) {
            if (_desktopTextFieldHandledDepth > 0 || IsSearchWidgetFocused() ||
                !TryGetFocusedTextEditor(rect, out var id, out var editor))
                return;

            var target = ObserveEditor(id, editor);
            if (target.Id != 0)
                LogTextEditor("FIELD impl-after", target, id, __result, editor, multiline);
        }

        private static bool TryGetFocusedTextEditor(Rect position, out int id,
            out TextEditor editor) {
            id = GUIUtility.keyboardControl;
            editor = null;
            if (id == 0)
                return false;

            editor = GUIUtility.GetStateObject(typeof(TextEditor), id) as TextEditor;
            if (editor == null)
                return false;
            if (editor.controlID == id && editor.position.Equals(position))
                return true;

            if (_controlIdForRect == null)
                return false;
            try {
                var rectControl = (int)_controlIdForRect.Invoke(null, new object[] { position });
                if (rectControl != id)
                    return false;

                editor = GUIUtility.GetStateObject(typeof(TextEditor), rectControl) as TextEditor;
                return editor != null && editor.controlID == rectControl;
            } catch (Exception exception) {
                if (DebugLogging)
                    WriteLog("FIELD impl control-id lookup failed " + exception.GetType().Name);
                editor = null;
                return false;
            }
        }

        private static void LogTextEditor(string stage, ControlToken target, int id, string text,
            TextEditor editor, bool multiline) {
            var currentEvent = Event.current;
            if (!DebugLogging || GUIUtility.keyboardControl != id || currentEvent == null ||
                (currentEvent.rawType != EventType.KeyDown &&
                    currentEvent.rawType != EventType.KeyUp))
                return;
            WriteLog(stage + " control=" + id + " token=" + target.Id + ":" +
                target.Generation + " keyboardControl=" + GUIUtility.keyboardControl +
                " hotControl=" + GUIUtility.hotControl + " text=[" + Escape(text) +
                "] editor=[" + Escape(editor.text) + "] cursor=" + editor.cursorIndex +
                " select=" + editor.selectIndex + " multiline=" + multiline + " " +
                DescribeEvent());
        }

        private static void BeforeQuickSearch(QuickSearchWidget __instance, out string __state) {
            var filter = __instance.filter;
            __state = filter.Text;
            if (__instance.CurrentlyFocused()) {
                _quickSearchFocusFrame = Time.frameCount;
                _searchWidgetFocused = true;
                _textFieldActive = true;
                BeginOrRefreshQuickSearchTarget(filter);
                Composition.Focus(_quickSearchTarget, _quickSearchCommittedText.Length,
                    _quickSearchCommittedText.Length);
                UpdateNativeFocus(true);
                ProcessNativeKeyEvent(Event.current, "quick-search");
                SetImeCompositionMode(true, "quick-search");
                ApplyQuickSearchComposition();
            }
        }

        private static void AfterQuickSearch(QuickSearchWidget __instance, string __state) {
            var focused = __instance.CurrentlyFocused();
            if (focused && _quickSearchCaretSyncPending) {
                SyncQuickSearchCaret(_quickSearchFilter, _quickSearchPendingCaret);
                _quickSearchCaretSyncPending = false;
                _quickSearchPendingCaret = -1;
            }
            if (!focused && ReferenceEquals(_quickSearchFilter, __instance.filter)) {
                SetQuickSearchText(_quickSearchFilter, _quickSearchCommittedText);
                _quickSearchFilter = null;
                _quickSearchTarget = default;
                _quickSearchRenderedText = "";
                _quickSearchCaretSyncPending = false;
                _quickSearchPendingCaret = -1;
                Composition.Blur();
            }
            if (!DebugLogging)
                return;
            var currentEvent = Event.current;
            var keyEvent = currentEvent != null && (currentEvent.rawType == EventType.KeyDown ||
                currentEvent.rawType == EventType.KeyUp);
            if (__state == __instance.filter.Text && (!focused || !keyEvent))
                return;
            WriteLog("SEARCH filter=" + RuntimeHelpers.GetHashCode(__instance.filter) +
                " before=[" + Escape(__state) + "] after=[" + Escape(__instance.filter.Text) +
                "] focused=" + focused + " " + DescribeEvent());
        }

        private static void BeforeSearchTextSet(QuickSearchFilter __instance, ref string value) {
            var previous = __instance.Text;
            if (_settingQuickSearchText)
                return;

            var currentEvent = Event.current;
            var fallbackCharacter = currentEvent != null && currentEvent.character >= ' ' &&
                currentEvent.character != '\u007f' &&
                currentEvent.rawType != EventType.KeyDown &&
                currentEvent.rawType != EventType.KeyUp;
            if (_textFieldActive && fallbackCharacter &&
                ProcessNativeTextCharacter(currentEvent, "quick-search")) {
                value = previous;
                if (DebugLogging)
                    WriteLog("SEARCH suppress-native-char char=U+" +
                        ((int)currentEvent.character).ToString("X4") + " " + DescribeEvent());
            }

            if (ReferenceEquals(__instance, _quickSearchFilter) &&
                value != _quickSearchRenderedText) {
                _quickSearchCommittedText = value ?? "";
                _quickSearchRenderedText = _quickSearchCommittedText;
                if (Composition.ActiveContext != 0)
                    Composition.CancelComposition(Composition.ActiveContext);
            }
            if (DebugLogging)
                WriteLog("SEARCH set filter=" + RuntimeHelpers.GetHashCode(__instance) + " old=[" +
                    Escape(previous) + "] new=[" + Escape(value) + "] " + DescribeEvent());
        }

        private static void BeginOrRefreshQuickSearchTarget(QuickSearchFilter filter) {
            if (!ReferenceEquals(filter, _quickSearchFilter)) {
                _quickSearchFilter = filter;
                _quickSearchTarget = new ControlToken(int.MinValue, ++_focusGeneration);
                _quickSearchCommittedText = filter.Text ?? "";
                _quickSearchRenderedText = _quickSearchCommittedText;
                if (DebugLogging)
                    WriteLog("STATE quick-search focus filter=" +
                        RuntimeHelpers.GetHashCode(filter) + " token=" + _quickSearchTarget.Id +
                        ":" + _quickSearchTarget.Generation + " text=[" +
                        Escape(_quickSearchCommittedText) + "]");
                return;
            }

            var currentText = filter.Text ?? "";
            if (currentText != _quickSearchRenderedText && !_settingQuickSearchText) {
                _quickSearchCommittedText = currentText;
                _quickSearchRenderedText = currentText;
            }
        }

        private static void ApplyQuickSearchComposition() {
            if (_quickSearchFilter == null || _quickSearchTarget.Id == 0)
                return;

            var actions = Composition.TakeActions(_quickSearchTarget, Stopwatch.GetTimestamp());
            var caret = _quickSearchCommittedText.Length;
            foreach (var action in actions) {
                var start = Math.Max(0, Math.Min(action.SelectionStart,
                    _quickSearchCommittedText.Length));
                var end = Math.Max(start, Math.Min(action.SelectionEnd,
                    _quickSearchCommittedText.Length));
                _quickSearchCommittedText = TextEditMath.ReplaceRange(_quickSearchCommittedText,
                    start, end, action.Text);
                caret = start + action.Text.Length;
                if (DebugLogging)
                    WriteLog("SEARCH commit token=" + action.Target.Id + ":" +
                        action.Target.Generation + " text=[" + Escape(action.Text) +
                        "] result=[" + Escape(_quickSearchCommittedText) + "]");
            }

            var renderedText = _quickSearchCommittedText;
            if (Composition.TryGetView(_quickSearchTarget, out var view)) {
                var start = Math.Max(0, Math.Min(view.SelectionStart,
                    _quickSearchCommittedText.Length));
                var end = Math.Max(start, Math.Min(view.SelectionEnd,
                    _quickSearchCommittedText.Length));
                renderedText = TextEditMath.ReplaceRange(_quickSearchCommittedText, start, end,
                    view.Text);
                caret = start + Math.Max(0, Math.Min(view.Cursor, view.Text.Length));
            }
            if (renderedText == _quickSearchFilter.Text)
                return;

            SetQuickSearchText(_quickSearchFilter, renderedText);
            SyncQuickSearchCaret(_quickSearchFilter, caret);
            _quickSearchCaretSyncPending = true;
            _quickSearchPendingCaret = caret;
            if (DebugLogging)
                WriteLog("SEARCH composition display=[" + Escape(renderedText) + "] committed=[" +
                    Escape(_quickSearchCommittedText) + "] preedit=" +
                    Composition.TryGetView(_quickSearchTarget, out _) + " " + DescribeEvent());
        }

        private static void SyncQuickSearchCaret(QuickSearchFilter filter, int caret) {
            if (filter == null)
                return;
            var control = GUIUtility.keyboardControl;
            if (control == 0)
                return;
            var editor = GUIUtility.GetStateObject(typeof(TextEditor), control) as TextEditor;
            if (editor == null)
                return;
            var text = filter.Text ?? "";
            editor.text = text;
            editor.cursorIndex = Math.Max(0, Math.Min(caret, text.Length));
            editor.selectIndex = editor.cursorIndex;
        }

        private static void SetQuickSearchText(QuickSearchFilter filter, string text) {
            if (filter == null)
                return;
            _settingQuickSearchText = true;
            try {
                filter.Text = text ?? "";
                _quickSearchRenderedText = filter.Text ?? "";
                GUI.changed = true;
            } finally {
                _settingQuickSearchText = false;
            }
        }

        private static void AfterKeyBindingIsDown(KeyBindingDef __instance, ref bool __result) {
            RecoverKeyBinding(__instance, ref __result, IsCameraDolly(__instance), pressed: false);
        }

        private static void AfterKeyBindingKeyDownEvent(KeyBindingDef __instance,
            ref bool __result) {
            var rotate = __instance == KeyBindingDefOf.Designator_RotateLeft ||
                __instance == KeyBindingDefOf.Designator_RotateRight;
            RecoverKeyBinding(__instance, ref __result, rotate, pressed: true);
        }

        private static void AfterPlaySettingsControls(bool worldView) {
            if (!TryRecoverKeyBinding(KeyBindingDefOf.OpenMapSearch, pressed: true))
                return;
            if (_lastZContext != 0 && _unboundPreeditContext == _lastZContext &&
                _unboundPreedit == "ㅋ") {
                ShortcutCommits.Arm(_lastZContext, _unboundPreedit);
                _unboundPreeditContext = 0;
                _unboundPreedit = "";
            }
            if (DebugLogging)
                WriteLog("RECOVER binding=" + KeyBindingDefOf.OpenMapSearch.defName + " " +
                    DescribeEvent());
            if (worldView)
                Find.WindowStack.Add(new Dialog_WorldSearch());
            else if (Find.CurrentMap != null)
                Find.WindowStack.Add(new Dialog_MapSearch(Find.CurrentMap));
        }

        private static void RecoverKeyBinding(KeyBindingDef bindingDef, ref bool result,
            bool gameplayShortcut, bool pressed) {
            if (result || !gameplayShortcut)
                return;
            result = TryRecoverKeyBinding(bindingDef, pressed);
            if (result && pressed && DebugLogging)
                WriteLog("RECOVER binding=" + bindingDef.defName + " " + DescribeEvent());
        }

        private static bool TryRecoverKeyBinding(KeyBindingDef bindingDef, bool pressed) {
            if (_textFieldActive || IsSearchWidgetFocused())
                return false;
            var preferences = KeyPrefs.KeyPrefsData;
            if (preferences == null || !preferences.keyPrefs.TryGetValue(bindingDef, out var binding))
                return false;
            return pressed
                ? GameplayKeyRecovery.ShouldRecoverPress(false, _textFieldActive, true,
                    (int)binding.keyBindingA, (int)binding.keyBindingB, GameplayKeys,
                    Stopwatch.GetTimestamp())
                : GameplayKeyRecovery.ShouldRecover(false, _textFieldActive, true,
                    (int)binding.keyBindingA, (int)binding.keyBindingB, GameplayKeys);
        }

        private static bool IsCameraDolly(KeyBindingDef binding) {
            return binding == KeyBindingDefOf.MapDolly_Left ||
                binding == KeyBindingDefOf.MapDolly_Right ||
                binding == KeyBindingDefOf.MapDolly_Up ||
                binding == KeyBindingDefOf.MapDolly_Down;
        }

        private static void SuppressRawHangulKey(int id) {
            var currentEvent = Event.current;
            var letter = IsHangulLetter(currentEvent.keyCode);
            var backspace = currentEvent.keyCode == KeyCode.Backspace;
            if (currentEvent.type != EventType.KeyDown || (!letter && !backspace))
                return;
            var shortcutModifiers = EventModifiers.Control | EventModifiers.Command | EventModifiers.Alt;
            var suppress = InputSuppression.ShouldSuppress(
                focusedTextField: GUIUtility.keyboardControl == id,
                hangul: _engine == "hangul",
                letter: letter,
                backspace: backspace,
                hasPreedit: Composition.HasPreedit,
                shortcut: (currentEvent.modifiers & shortcutModifiers) != 0);
            if (DebugLogging)
                WriteLog("KEY textfield control=" + id + " key=" + currentEvent.keyCode +
                    " char=U+" + ((int)currentEvent.character).ToString("X4") +
                    " modifiers=" + currentEvent.modifiers + " engine=" + _engine +
                    " preedit=" + Composition.HasPreedit + " suppress=" + suppress);
            if (suppress)
                currentEvent.Use();
        }

        private static void SuppressCommittedCharacter(ControlToken target, int id) {
            var currentEvent = Event.current;
            if (currentEvent == null ||
                !CommittedCharacters.ShouldSuppress(target, currentEvent.character, Time.frameCount))
                return;
            if (DebugLogging)
                WriteLog("KEY duplicate-commit control=" + id + " char=U+" +
                    ((int)currentEvent.character).ToString("X4") + " " + DescribeEvent());
            currentEvent.character = '\0';
        }

        private static void SetImeCompositionMode(bool textFieldActive, string reason) {
            var requested = textFieldActive ? IMECompositionMode.On : _imeModeWithoutTextField;
            var previous = Input.imeCompositionMode;
            if (previous == requested)
                return;
            Input.imeCompositionMode = requested;
            if (DebugLogging)
                WriteLog("IME mode " + previous + " -> " + Input.imeCompositionMode + " reason=" +
                    reason + " keyboardControl=" + GUIUtility.keyboardControl +
                    " focusedControl=" + _focusedControl + " seenFrame=" +
                    _focusedTextFieldFrame + " frame=" + Time.frameCount);
        }

        private static void UpdateNativeFocus(bool focused) {
            if (!_nativeDirectInputAvailable || !_nativeReady || _nativeSetFocus == null)
                return;
            var now = Stopwatch.GetTimestamp();
            if (_nativeFocusState == focused || now < _nativeFocusRetryAt)
                return;
            var result = _nativeSetFocus(focused ? 1 : 0);
            if (result == 0) {
                _nativeFocusState = focused;
                _nativeFocusRetryAt = 0;
                if (DebugLogging)
                    WriteLog("FCITX focus=" + focused);
            } else {
                _nativeFocusRetryAt = now + Stopwatch.Frequency;
                if (DebugLogging)
                    WriteLog("FCITX focus failed focused=" + focused + " result=" + result);
            }
        }

        private static bool ProcessNativeKeyEvent(Event currentEvent, string source) {
            if (!_nativeDirectInputAvailable || !_nativeReady || !_textFieldActive ||
                currentEvent == null || _nativeProcessKey == null)
                return false;
            var rawType = currentEvent.rawType;
            if (rawType != EventType.KeyDown && rawType != EventType.KeyUp)
                return false;
            var release = rawType == EventType.KeyUp;
            var hasEventKey = TryGetFcitxKey(currentEvent, out var keyval);
            if (TryConsumeWaylithRoutedEvent(currentEvent, hasEventKey, keyval, release,
                    out var waylithAccepted)) {
                if (waylithAccepted)
                    currentEvent.Use();
                if (DebugLogging)
                    WriteLog("FCITX key source=" + source + " forwarded=waylith " +
                        "release=" + release + " accepted=" + waylithAccepted + " " +
                        DescribeEvent());
                return waylithAccepted;
            }
            if (!hasEventKey)
                return false;

            var keyName = currentEvent.keyCode.ToString();
            var state = FcitxModifierState(currentEvent.modifiers);
            if (!release) {
                var keyDownState = RemoveModifierSelfState(keyval, state);
                var accepted = SendNativeKey(keyval, keyDownState, release: false);
                NativePressedKeys.Add(keyval);
                if (accepted) {
                    NativeConsumedKeys.Add(keyval);
                    currentEvent.Use();
                }
                if (DebugLogging)
                    WriteLog("FCITX key source=" + source + " key=" + keyName + " keysym=0x" +
                        keyval.ToString("X") + " state=0x" + keyDownState.ToString("X") +
                        " rawState=0x" + state.ToString("X") +
                        " release=False accepted=" + accepted + " " + DescribeEvent());
                return accepted;
            }

            var consumed = NativeConsumedKeys.Remove(keyval);
            if (NativePressedKeys.Remove(keyval)) {
                var accepted = SendNativeKey(keyval, state, release: true);
                if (consumed)
                    currentEvent.Use();
                if (DebugLogging)
                    WriteLog("FCITX key source=" + source + " key=" + keyName + " keysym=0x" +
                        keyval.ToString("X") + " state=0x" + state.ToString("X") +
                        " release=True accepted=" + accepted + " consumed=" + consumed + " " +
                        DescribeEvent());
                return consumed;
            }

            if ((currentEvent.modifiers & (EventModifiers.Control | EventModifiers.Alt |
                    EventModifiers.Command)) == 0 && !IsModifierKeysym(keyval) &&
                !IsNonTextKeysym(keyval))
                return false;
            var pressState = RemoveModifierSelfState(keyval, state);
            var syntheticAccepted = SendNativeKey(keyval, pressState, release: false);
            SendNativeKey(keyval, state, release: true);
            if (syntheticAccepted)
                currentEvent.Use();
            if (DebugLogging)
                WriteLog("FCITX key synthetic source=" + source + " key=" + keyName +
                    " keysym=0x" + keyval.ToString("X") + " state=0x" +
                    pressState.ToString("X") + " accepted=" + syntheticAccepted + " " +
                    DescribeEvent());
            return syntheticAccepted;
        }

        private static bool ProcessNativeTextCharacter(Event currentEvent, string source) {
            if (!_nativeDirectInputAvailable || !_nativeReady || !_textFieldActive ||
                currentEvent == null || _nativeProcessKey == null ||
                currentEvent.character < ' ' || currentEvent.character == '\u007f' ||
                currentEvent.character >= '\ud800' && currentEvent.character <= '\udfff')
                return false;
            if (currentEvent.rawType == EventType.KeyDown ||
                currentEvent.rawType == EventType.KeyUp)
                return false;
            var keyval = UnicodeKeysym(currentEvent.character);
            var state = FcitxModifierState(currentEvent.modifiers);
            var accepted = SendNativeKey(keyval, state, release: false);
            SendNativeKey(keyval, state, release: true);
            if (DebugLogging)
                WriteLog("FCITX character source=" + source + " char=U+" +
                    ((int)currentEvent.character).ToString("X4") + " keysym=0x" +
                    keyval.ToString("X") + " state=0x" + state.ToString("X") +
                    " accepted=" + accepted + " " + DescribeEvent());
            return accepted;
        }

        private static bool TryConsumeWaylithRoutedEvent(Event currentEvent, bool hasEventKey,
            uint keyval, bool release, out bool accepted) {
            accepted = false;
            for (var index = 0; index < WaylithRoutedKeyEvents.Count; index++) {
                var routed = WaylithRoutedKeyEvents[index];
                if (routed.Release != release || Time.frameCount - routed.Frame > 1)
                    continue;

                var matches = hasEventKey
                    ? routed.Keyval == keyval
                    : currentEvent.keyCode == KeyCode.None && currentEvent.character == '\0' &&
                        routed.Modifier;
                if (!matches)
                    continue;

                accepted = routed.Accepted;
                WaylithRoutedKeyEvents.RemoveAt(index);
                return true;
            }
            return false;
        }

        private static bool SendNativeKey(uint keyval, uint state, bool release) {
            if (_nativeProcessKey == null)
                return false;
            var result = _nativeProcessKey(keyval, 0, state, release ? 1 : 0,
                unchecked((uint)Environment.TickCount));
            return result == 1;
        }

        private static bool TryGetFcitxKey(Event currentEvent, out uint keyval) {
            var name = currentEvent.keyCode.ToString();
            var character = currentEvent.character;
            if (name == "None" && character >= ' ' && character != '\u007f' &&
                !(character >= '\ud800' && character <= '\udfff')) {
                keyval = UnicodeKeysym(character);
                return true;
            }
            if (TryGetFcitxKey(name, out keyval))
                return true;
            if (character >= ' ' && character != '\u007f' &&
                !(character >= '\ud800' && character <= '\udfff')) {
                keyval = UnicodeKeysym(character);
                return true;
            }
            keyval = 0;
            return false;
        }

        private static bool TryGetFcitxKey(string name, out uint keyval) {
            if (string.IsNullOrEmpty(name)) {
                keyval = 0;
                return false;
            }
            if (name.Length == 1 && char.IsLetter(name[0])) {
                keyval = char.ToLowerInvariant(name[0]);
                return true;
            }
            if ((name.StartsWith("Alpha", StringComparison.Ordinal) && name.Length == 6) ||
                (name.StartsWith("Digit", StringComparison.Ordinal) && name.Length == 6)) {
                var digit = name[5];
                if (char.IsDigit(digit)) {
                    keyval = (uint)digit;
                    return true;
                }
            }
            if ((name.StartsWith("Keypad", StringComparison.Ordinal) && name.Length == 7) ||
                (name.StartsWith("Numpad", StringComparison.Ordinal) && name.Length == 7) ||
                (name.StartsWith("Kp", StringComparison.Ordinal) && name.Length == 3)) {
                var digit = name[name.Length - 1];
                if (char.IsDigit(digit)) {
                    keyval = 0xffb0u + (uint)(digit - '0');
                    return true;
                }
            }

            switch (name) {
                case "Space": keyval = 0x20; return true;
                case "Return":
                case "Enter": keyval = 0xff0d; return true;
                case "KeypadEnter":
                case "NumpadEnter":
                case "KpEnter": keyval = 0xff8d; return true;
                case "Tab": keyval = 0xff09; return true;
                case "Backspace": keyval = 0xff08; return true;
                case "Escape": keyval = 0xff1b; return true;
                case "Delete": keyval = 0xffff; return true;
                case "Insert": keyval = 0xff63; return true;
                case "Home": keyval = 0xff50; return true;
                case "End": keyval = 0xff57; return true;
                case "PageUp": keyval = 0xff55; return true;
                case "PageDown": keyval = 0xff56; return true;
                case "LeftArrow": keyval = 0xff51; return true;
                case "UpArrow": keyval = 0xff52; return true;
                case "RightArrow": keyval = 0xff53; return true;
                case "DownArrow": keyval = 0xff54; return true;
                case "Left": keyval = 0xff51; return true;
                case "Up": keyval = 0xff52; return true;
                case "Right": keyval = 0xff53; return true;
                case "Down": keyval = 0xff54; return true;
                case "LeftShift": keyval = 0xffe1; return true;
                case "RightShift": keyval = 0xffe2; return true;
                case "LeftControl": keyval = 0xffe3; return true;
                case "LeftCtrl": keyval = 0xffe3; return true;
                case "RightControl": keyval = 0xffe4; return true;
                case "RightCtrl": keyval = 0xffe4; return true;
                case "LeftAlt": keyval = 0xffe9; return true;
                case "RightAlt": keyval = 0xffea; return true;
                case "LeftCommand": keyval = 0xffeb; return true;
                case "LeftMeta":
                case "LeftSuper": keyval = 0xffeb; return true;
                case "RightCommand": keyval = 0xffec; return true;
                case "RightMeta":
                case "RightSuper": keyval = 0xffec; return true;
                case "ContextMenu":
                case "Menu": keyval = 0xff67; return true;
                case "CapsLock": keyval = 0xffe5; return true;
                case "NumLock": keyval = 0xff7f; return true;
                case "ScrollLock": keyval = 0xff14; return true;
                case "PrintScreen": keyval = 0xff61; return true;
                case "Pause": keyval = 0xff13; return true;
                case "Backquote": keyval = 0x60; return true;
                case "Quote": keyval = 0x27; return true;
                case "Semicolon":
                case "OEM1": keyval = 0x3b; return true;
                case "Comma": keyval = 0x2c; return true;
                case "Period": keyval = 0x2e; return true;
                case "Slash":
                case "OEM2": keyval = 0x2f; return true;
                case "Backslash": keyval = 0x5c; return true;
                case "LeftBracket": keyval = 0x5b; return true;
                case "RightBracket": keyval = 0x5d; return true;
                case "Minus": keyval = 0x2d; return true;
                case "Equals": keyval = 0x3d; return true;
                case "Hangul":
                case "Hangeul": keyval = 0xff31; return true;
                case "HangulHanja":
                case "Hanja": keyval = 0xff34; return true;
            }
            if (name.Length >= 2 && name[0] == 'F' &&
                int.TryParse(name.Substring(1), out var functionKey) &&
                functionKey >= 1 && functionKey <= 35) {
                keyval = 0xffbeu + (uint)(functionKey - 1);
                return true;
            }
            keyval = 0;
            return false;
        }

        private static uint UnicodeKeysym(char character) {
            return character <= 0xff ? character : 0x01000000u | character;
        }

        private static uint FcitxModifierState(EventModifiers modifiers) {
            uint state = 0;
            if ((modifiers & EventModifiers.Shift) != 0) state |= 1u << 0;
            if ((modifiers & EventModifiers.CapsLock) != 0) state |= 1u << 1;
            if ((modifiers & EventModifiers.Control) != 0) state |= 1u << 2;
            if ((modifiers & EventModifiers.Alt) != 0) state |= 1u << 3;
            if ((modifiers & EventModifiers.Command) != 0) state |= 1u << 6;
            return state;
        }

        private static bool IsModifierKeysym(uint keyval) {
            return keyval == 0xffe1 || keyval == 0xffe2 ||
                keyval == 0xffe3 || keyval == 0xffe4 ||
                keyval == 0xffe9 || keyval == 0xffea ||
                keyval == 0xffeb || keyval == 0xffec;
        }

        private static bool IsNonTextKeysym(uint keyval) {
            return keyval >= 0xff00;
        }

        private static uint RemoveModifierSelfState(uint keyval, uint state) {
            if (keyval == 0xffe1 || keyval == 0xffe2)
                return state & ~(1u << 0);
            if (keyval == 0xffe3 || keyval == 0xffe4)
                return state & ~(1u << 2);
            if (keyval == 0xffe9 || keyval == 0xffea)
                return state & ~((1u << 3) | (1u << 7));
            if (keyval == 0xffeb || keyval == 0xffec)
                return state & ~(1u << 6);
            return state;
        }

        private static void LogRootEvent(Event currentEvent, bool textFieldActive) {
            if (!DebugLogging || (currentEvent.rawType != EventType.KeyDown &&
                currentEvent.rawType != EventType.KeyUp))
                return;
            WriteLog("ROOT " + DescribeEvent() + " engine=" + _engine + " ime=" +
                Input.imeCompositionMode + " textFieldActive=" + textFieldActive +
                " keyboardControl=" + GUIUtility.keyboardControl + " focusedControl=" +
                _focusedControl + " compositionContext=" + Composition.ActiveContext +
                " preedit=" + Composition.HasPreedit + " pending=" + Composition.PendingCount +
                (currentEvent.keyCode == KeyCode.None ? " unresolved-keycode" : ""));
        }

        private static void LogTextField(string stage, ControlToken target, int id, GUIContent content,
            TextEditor editor) {
            var currentEvent = Event.current;
            if (!DebugLogging || GUIUtility.keyboardControl != id || currentEvent == null ||
                (currentEvent.rawType != EventType.KeyDown &&
                    currentEvent.rawType != EventType.KeyUp))
                return;
            WriteLog(stage + " control=" + id + " token=" + target.Id + ":" + target.Generation +
                " keyboardControl=" + GUIUtility.keyboardControl + " hotControl=" +
                GUIUtility.hotControl + " contentObject=" + RuntimeHelpers.GetHashCode(content) +
                " editorObject=" + RuntimeHelpers.GetHashCode(editor) + " content=[" +
                Escape(content.text) + "] editor=[" + Escape(editor.text) + "] cursor=" +
                editor.cursorIndex + " select=" + editor.selectIndex +
                //" graphicalCursor=" + editor.graphicalCursorPos +
                " multiline=" + editor.multiline + " guiChanged=" +
                GUI.changed + " " + DescribeEvent());
        }

        private static string DescribeEvent() {
            var currentEvent = Event.current;
            return currentEvent == null
                ? "event=null frame=" + Time.frameCount
                : "event=" + currentEvent.type + " raw=" + currentEvent.rawType + " key=" +
                    currentEvent.keyCode + " char=U+" + ((int)currentEvent.character).ToString("X4") +
                    " modifiers=" + currentEvent.modifiers + " command=[" +
                    Escape(currentEvent.commandName) + "] frame=" + Time.frameCount;
        }

        private static void VerifyCommittedText(ControlToken target, int id, GUIContent content,
            TextEditor editor) {
            if (!ExpectedFieldTexts.TryGetValue(target, out var expected))
                return;
            ExpectedFieldTexts.Remove(target);
            if (DebugLogging)
                WriteLog("VERIFY commit control=" + id + " expected=[" + Escape(expected) +
                    "] content=[" + Escape(content.text) + "] editor=[" + Escape(editor.text) +
                    "] event=" + Event.current.type);
        }

        private static void BeforeDrawTextAreaContent(ref string text, ref int caret, ref int selA, ref int selB, ref int compA, ref int compB) {
            var currentEvent = Event.current;
            var logEvent = currentEvent != null &&
                (currentEvent.rawType == EventType.KeyDown ||
                    currentEvent.rawType == EventType.KeyUp ||
                    (currentEvent.type == EventType.Used && currentEvent.character != '\0'));
            var id = GUIUtility.keyboardControl;
            if (id == 0) {
                if (DebugLogging && logEvent)
                    WriteLog("DRAW hook skip=no-keyboard-control " + DescribeEvent());
                return;
            }
            if (!ControlTokens.TryGetValue(id, out var target)) {
                if (DebugLogging && logEvent)
                    WriteLog("DRAW hook skip=no-control-token control=" + id + " " + DescribeEvent());
                return;
            }
            if (!Composition.TryGetView(target, out var view)) {
                if (DebugLogging && logEvent)
                    WriteLog("DRAW hook skip=no-preedit control=" + id + " token=" +
                        target.Id + ":" + target.Generation + " " + DescribeEvent());
                return;
            }

            if (DebugLogging && logEvent)
                WriteLog("DRAW hook apply control=" + id + " token=" + target.Id + ":" +
                    target.Generation + " preedit=[" + Escape(view.Text) + "] " + DescribeEvent());

            var originalText = text ?? "";
            var selectionStart = Math.Max(0, Math.Min(view.SelectionStart, originalText.Length));
            var selectionEnd = Math.Max(selectionStart, Math.Min(view.SelectionEnd, originalText.Length));

            text = TextEditMath.ReplaceRange(originalText, selectionStart, selectionEnd, view.Text);

            var preeditCursor = Math.Max(0, Math.Min(view.Cursor, view.Text.Length));
            caret = selectionStart + preeditCursor;

            selA = caret;
            selB = caret;
            compA = selectionStart;
            compB = selectionStart + view.Text.Length;
        }

        private static ControlToken ObserveEditor(int id, TextEditor editor) {
            if (GUIUtility.keyboardControl == id) {
                if (_focusedControl != id || !ControlTokens.TryGetValue(id, out var focused)) {
                    _focusedControl = id;
                    focused = new ControlToken(id, ++_focusGeneration);
                    ControlTokens[id] = focused;
                    if (DebugLogging)
                        WriteLog("STATE focus control=" + id + " generation=" + focused.Generation);
                }
                _focusedTextFieldFrame = Time.frameCount;
                _textFieldActive = true;
                SetImeCompositionMode(true, "text-field");
                Composition.Focus(focused, editor.cursorIndex, editor.selectIndex);
                return focused;
            }
            return ControlTokens.TryGetValue(id, out var target) ? target : default;
        }

        private static int ApplyAction(TextEditor editor, int maxLength, CommitAction action,
            StringBuilder inserted) {
            var selectionStart = Math.Max(0, Math.Min(action.SelectionStart, editor.text.Length));
            var selectionEnd = Math.Max(selectionStart, Math.Min(action.SelectionEnd, editor.text.Length));
            var previousCursor = editor.cursorIndex;
            var previousSelect = editor.selectIndex;
            var cursorMoved = Math.Min(previousCursor, previousSelect) != selectionStart ||
                Math.Max(previousCursor, previousSelect) != selectionEnd;
            editor.cursorIndex = selectionEnd;
            editor.selectIndex = selectionStart;
            var selectedLength = selectionEnd - selectionStart;
            var allowedLength = maxLength < 0
                ? action.Text.Length
                : Math.Max(0, maxLength - (editor.text.Length - selectedLength));
            var insertedLength = Math.Min(action.Text.Length, allowedLength);
            for (var i = 0; i < insertedLength; i++) {
                editor.Insert(action.Text[i]);
                inserted?.Append(action.Text[i]);
            }
            if (cursorMoved) {
                editor.cursorIndex = Math.Max(0, Math.Min(editor.text.Length,
                    TextEditMath.TransformIndex(previousCursor, selectionStart, selectionEnd,
                        insertedLength)));
                editor.selectIndex = Math.Max(0, Math.Min(editor.text.Length,
                    TextEditMath.TransformIndex(previousSelect, selectionStart, selectionEnd,
                        insertedLength)));
            }
            return insertedLength;
        }

        private static bool IsHangulLetter(KeyCode keyCode) {
            return keyCode >= KeyCode.A && keyCode <= KeyCode.Z;
        }

        private static bool ContainsNonAscii(string text) {
            foreach (var character in text) {
                if (character > 127)
                    return true;
            }
            return false;
        }

        private static string DecodeHex(string hex) {
            return Encoding.UTF8.GetString(DecodeHexBytes(hex));
        }

        private static byte[] DecodeHexBytes(string hex) {
            if ((hex.Length & 1) != 0)
                throw new FormatException("Odd hex length: " + hex.Length);
            var bytes = new byte[hex.Length / 2];
            for (var i = 0; i < bytes.Length; i++)
                bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return bytes;
        }

        private static string Escape(string text) {
            if (text == null)
                return "null";
            return text.Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n");
        }

        private static void Shutdown() {
            if (Interlocked.Exchange(ref _shuttingDown, 1) != 0)
                return;
            Application.quitting -= Shutdown;
            _restartAt = 0;
            _nativeStop?.Invoke();
            _nativeLoaded = false;
            lock (LogLock) {
                _log?.Dispose();
                _log = null;
            }
        }

        private static void EnsureLog() {
            if (!DebugLogging || _log != null)
                return;
            var append = _logInitialized && (!File.Exists(LogPath) ||
                new FileInfo(LogPath).Length < LogMaxBytes);
            _log = new StreamWriter(LogPath, append, new UTF8Encoding(false), LogBufferSize);
            _lastLogFlush = Stopwatch.GetTimestamp();
            _logInitialized = true;
        }

        private static void WriteLog(string message) {
            if (!DebugLogging)
                return;
            lock (LogLock) {
                EnsureLog();
                if (_log == null)
                    return;
                _log.WriteLine(DateTimeOffset.Now.ToString("O") + " " + message);
                var now = Stopwatch.GetTimestamp();
                if (now - _lastLogFlush < LogFlushInterval)
                    return;
                _log.Flush();
                _lastLogFlush = now;
                if (_log.BaseStream.Position < LogMaxBytes)
                    return;
                _log.Dispose();
                _log = new StreamWriter(LogPath, false, new UTF8Encoding(false), LogBufferSize);
                _log.WriteLine(DateTimeOffset.Now.ToString("O") + " LOG rotated");
            }
        }
    }
}
