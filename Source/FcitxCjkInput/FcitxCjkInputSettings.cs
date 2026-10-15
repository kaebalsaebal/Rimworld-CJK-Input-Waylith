using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace FcitxCjkInput {
    public sealed class FcitxCjkInputSettings : ModSettings {
        public bool DebugLog;
        public RightAltKeyMode RightAltMode = RightAltKeyMode.AltRight;

        public override void ExposeData() {
            Scribe_Values.Look(ref DebugLog, "debugLog", false);
            Scribe_Values.Look(ref RightAltMode, "rightAltMode", RightAltKeyMode.AltRight);
        }
    }

    public sealed class FcitxCjkInputEntry : Mod {
        internal static FcitxCjkInputSettings Settings { get; private set; }

        public FcitxCjkInputEntry(ModContentPack content) : base(content) {
            Settings = GetSettings<FcitxCjkInputSettings>();
        }

        public override string SettingsCategory() {
            return "Fcitx CJK Input";
        }

        private static string RightAltModeLabel(RightAltKeyMode mode) {
            switch (mode) {
                case RightAltKeyMode.Hangul: return "Hangul (Korean input toggle)";
                case RightAltKeyMode.HangulHanja: return "HangulHanja (Korean Hanja conversion)";
                case RightAltKeyMode.Kanji: return "Kanji (Japanese)";
                case RightAltKeyMode.Muhenkan: return "Muhenkan (Japanese)";
                case RightAltKeyMode.Henkan: return "Henkan (Japanese conversion)";
                case RightAltKeyMode.HiraganaKatakana: return "HiraganaKatakana (Japanese)";
                case RightAltKeyMode.ZenkakuHankaku: return "ZenkakuHankaku (Japanese full/half width)";
                case RightAltKeyMode.KanaLock: return "KanaLock (Japanese)";
                case RightAltKeyMode.KanaShift: return "KanaShift (Japanese)";
                case RightAltKeyMode.EisuShift: return "EisuShift (Japanese alphanumeric)";
                case RightAltKeyMode.EisuToggle: return "EisuToggle (Japanese alphanumeric)";
                case RightAltKeyMode.ModeSwitch: return "ModeSwitch (generic)";
                case RightAltKeyMode.AltRight:
                default: return "AltRight (standard right Alt)";
            }
        }

        public override void DoSettingsWindowContents(Rect inRect) {
            var listing = new Listing_Standard();
            listing.Begin(inRect);
            var enabled = Settings.DebugLog;
            listing.CheckboxLabeled("Debug log", ref enabled,
                "Write verbose IME diagnostics to /tmp/fcitxcjkinput.log.");
            if (listing.ButtonText("Right Alt mapping: " + RightAltModeLabel(Settings.RightAltMode))) {
                var options = new List<FloatMenuOption>();
                foreach (RightAltKeyMode mode in Enum.GetValues(typeof(RightAltKeyMode))) {
                    var selectedMode = mode;
                    options.Add(new FloatMenuOption(RightAltModeLabel(selectedMode), () => {
                        Settings.RightAltMode = selectedMode;
                        WriteSettings();
                    }));
                }
                Find.WindowStack.Add(new FloatMenu(options));
            }
            listing.Label("Select the X11 keysym Fcitx should receive for Right Alt; " +
                "match this to the key configured in your input method.");
            listing.End();
            if (enabled != Settings.DebugLog) {
                Settings.DebugLog = enabled;
                WriteSettings();
                FcitxCjkInputMod.SetDebugLogging(enabled);
            }
        }
    }
}
