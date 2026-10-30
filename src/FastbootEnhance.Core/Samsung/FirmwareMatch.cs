using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace FastbootEnhance.Core.Samsung
{
    /// <summary>What a Samsung phone in Android says about its firmware over adb.</summary>
    public sealed class SamsungPhone
    {
        /// <summary>One adb shell command that prints each property as key=value.</summary>
        public const string Command =
            "echo maker=$(getprop ro.product.manufacturer); " +
            "echo model=$(getprop ro.product.model); " +
            "echo bootloader=$(getprop ro.boot.bootloader); " +
            "echo pda=$(getprop ro.build.PDA); " +
            "echo android=$(getprop ro.build.version.release); " +
            "echo sales=$(getprop ro.csc.sales_code); " +
            "echo cscver=$(getprop ril.official_cscver); " +
            "echo warranty=$(getprop ro.boot.warranty_bit); " +
            "echo warranty2=$(getprop ro.warranty_bit); " +
            "echo locked=$(getprop ro.boot.flash.locked); " +
            "echo vbstate=$(getprop ro.boot.verifiedbootstate)";

        public string Serial { get; set; }
        public string Maker { get; set; }
        /// <summary>"SM-S918B".</summary>
        public string Model { get; set; }
        /// <summary>The bootloader's build, "S918BXXU3BWJ1".</summary>
        public string Bootloader { get; set; }
        public string Android { get; set; }
        /// <summary>The phone's own region code, "KSA".</summary>
        public string SalesCode { get; set; }
        /// <summary>The CSC build in use, "S918BOXM3BWJ1"; its code is OXM.</summary>
        public string CscVersion { get; set; }

        /// <summary>
        /// Knox's warranty bit as the phone reports it: "0" intact, "1" tripped, null unknown.
        /// A root-hiding module can make a tripped phone report 0.
        /// </summary>
        public string Warranty { get; set; }

        /// <summary>
        /// True when the phone says its bootloader is unlocked (ro.boot.flash.locked 0, or
        /// verified boot "orange"); null when it says nothing. A module can hide this too.
        /// </summary>
        public bool? Unlocked { get; set; }

        public bool IsSamsung => string.Equals(Maker, "samsung", StringComparison.OrdinalIgnoreCase);

        /// <summary>The CSC code inside CscVersion (OXM), when it reads as one.</summary>
        public string CscCode
        {
            get
            {
                if (CscVersion == null || CscVersion.Length < 12)
                    return null;
                string v = CscVersion.Trim().ToUpperInvariant();
                SamsungBuild b = SamsungBuild.Parse(v);
                return b == null ? null : v.Substring(b.Model.Length, 3);
            }
        }

        /// <summary>"0" or "1"; anything else is no answer.</summary>
        static string Bit(string value)
        {
            value = value?.Trim();
            return value == "0" || value == "1" ? value : null;
        }

        public static SamsungPhone Parse(string serial, string output)
        {
            Dictionary<string, string> v = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string line in (output ?? "").Split('\n'))
            {
                int eq = line.IndexOf('=');
                if (eq > 0)
                {
                    string value = line.Substring(eq + 1).Trim();
                    if (value.Length > 0)
                        v[line.Substring(0, eq).Trim()] = value;
                }
            }
            string get(string k) => v.TryGetValue(k, out string s) ? s : null;
            return new SamsungPhone
            {
                Serial = serial,
                Maker = get("maker"),
                Model = get("model"),
                Bootloader = get("bootloader") ?? get("pda"),
                Android = get("android"),
                SalesCode = get("sales"),
                CscVersion = get("cscver"),
                Warranty = Bit(get("warranty")) ?? Bit(get("warranty2")),
                Unlocked = get("locked") == "0" || string.Equals(get("vbstate"), "orange", StringComparison.OrdinalIgnoreCase) ? true
                    : get("locked") == "1" ? false : (bool?)null,
            };
        }
    }

    public enum MatchLevel
    {
        Ok,
        Notice,
        Warn,
        Block,
    }

    public enum MatchCode
    {
        NotSamsung,
        SameModel,
        OtherModel,
        SameBinary,
        NewerBinary,
        OlderBinary,
        SameAndroid,
        NewerAndroid,
        OlderAndroid,
        SameCsc,
        OtherCsc,
        CscWipes,
        MixedFirmware,
        PhoneUnknown,

        /// <summary>Knox's warranty bit reads 0: nothing unofficial was ever flashed.</summary>
        KnoxIntact,

        /// <summary>Knox's warranty bit reads 1: tripped for good.</summary>
        KnoxTripped,

        /// <summary>
        /// Knox reads 0, but the bootloader is unlocked: if anything unofficial was flashed since,
        /// Knox is tripped and a module is hiding it.
        /// </summary>
        KnoxUnlocked,
    }

    public sealed class MatchFinding
    {
        public MatchLevel Level { get; internal set; }
        public MatchCode Code { get; internal set; }
        /// <summary>The firmware's value and the phone's, for the message.</summary>
        public string Firmware { get; internal set; }
        public string Phone { get; internal set; }
    }

    /// <summary>
    /// Firmware against the phone it is meant for, before Odin is ever opened: the same model, a
    /// bootloader binary the phone accepts, the Android version and the CSC. A Block means the
    /// phone would refuse the firmware (SW REV CHECK FAIL) or it is for another phone.
    /// </summary>
    public static class FirmwareMatch
    {
        public static List<MatchFinding> Check(FirmwareSet firmware, SamsungPhone phone)
        {
            List<MatchFinding> f = new List<MatchFinding>();
            void add(MatchLevel l, MatchCode c, string fw = null, string ph = null) =>
                f.Add(new MatchFinding { Level = l, Code = c, Firmware = fw, Phone = ph });

            if (firmware.Mixed.Count > 0)
                add(MatchLevel.Warn, MatchCode.MixedFirmware, string.Join(", ", firmware.Mixed));
            if (firmware.Get(FirmwarePart.CSC) != null)
                add(MatchLevel.Notice, MatchCode.CscWipes, firmware.Get(FirmwarePart.CSC).FileName,
                    firmware.Get(FirmwarePart.HomeCsc)?.FileName);

            if (phone == null || phone.Model == null)
            {
                add(MatchLevel.Notice, MatchCode.PhoneUnknown);
                return Sorted(f);
            }
            if (!phone.IsSamsung && phone.Maker != null)
            {
                add(MatchLevel.Block, MatchCode.NotSamsung, null, phone.Maker + " " + phone.Model);
                return Sorted(f);
            }

            string fwModel = Bare(firmware.Model), phModel = Bare(phone.Model);
            if (fwModel != null)
            {
                if (fwModel == phModel)
                    add(MatchLevel.Ok, MatchCode.SameModel, firmware.Model, phone.Model);
                else
                    add(MatchLevel.Block, MatchCode.OtherModel, firmware.Model, phone.Model);
            }

            SamsungBuild fw = firmware.Build, ph = SamsungBuild.Parse(phone.Bootloader);
            if (fw != null && ph != null)
            {
                string a = fw.BinaryChar.ToString(), b = ph.BinaryChar.ToString();
                if (fw.Binary < ph.Binary)
                    add(MatchLevel.Block, MatchCode.OlderBinary, a, b);
                else if (fw.Binary > ph.Binary)
                    add(MatchLevel.Notice, MatchCode.NewerBinary, a, b);
                else
                    add(MatchLevel.Ok, MatchCode.SameBinary, a, b);
            }

            int? phoneAndroid = Major(phone.Android);
            if (firmware.Android != null && phoneAndroid != null)
            {
                if (firmware.Android < phoneAndroid)
                    add(MatchLevel.Warn, MatchCode.OlderAndroid, firmware.Android.ToString(), phoneAndroid.ToString());
                else if (firmware.Android > phoneAndroid)
                    add(MatchLevel.Ok, MatchCode.NewerAndroid, firmware.Android.ToString(), phoneAndroid.ToString());
                else
                    add(MatchLevel.Ok, MatchCode.SameAndroid, firmware.Android.ToString(), phoneAndroid.ToString());
            }

            string fwCsc = firmware.CscCode, phCsc = phone.CscCode;
            if (fwCsc != null && phCsc != null)
            {
                string phoneText = phone.SalesCode != null ? phone.SalesCode + " (" + phCsc + ")" : phCsc;
                if (fwCsc == phCsc)
                    add(MatchLevel.Ok, MatchCode.SameCsc, fwCsc, phoneText);
                else
                    add(MatchLevel.Notice, MatchCode.OtherCsc, fwCsc, phoneText);
            }

            // Says nothing about the firmware itself; shown so a root or custom recovery is
            // never flashed without knowing what it costs.
            if (phone.Warranty == "0" && phone.Unlocked == true)
                add(MatchLevel.Notice, MatchCode.KnoxUnlocked, null, "0x0");
            else if (phone.Warranty == "0")
                add(MatchLevel.Ok, MatchCode.KnoxIntact, null, "0x0");
            else if (phone.Warranty == "1")
                add(MatchLevel.Notice, MatchCode.KnoxTripped, null, "0x1");
            return Sorted(f);
        }

        /// <summary>The most serious level among the findings.</summary>
        public static MatchLevel Overall(IEnumerable<MatchFinding> findings) =>
            findings.Select(x => x.Level).DefaultIfEmpty(MatchLevel.Ok).Max();

        static List<MatchFinding> Sorted(List<MatchFinding> f) =>
            f.OrderByDescending(x => x.Level).ToList();

        static string Bare(string model)
        {
            if (model == null)
                return null;
            string m = model.Trim().ToUpperInvariant();
            return m.StartsWith("SM-", StringComparison.Ordinal) ? m.Substring(3) : m;
        }

        static int? Major(string version)
        {
            Match m = Regex.Match(version ?? "", @"^\d+");
            return m.Success && int.TryParse(m.Value, out int v) ? v : (int?)null;
        }
    }
}
