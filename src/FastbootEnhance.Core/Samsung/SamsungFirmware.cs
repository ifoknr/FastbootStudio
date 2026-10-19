using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace FastbootEnhance.Core.Samsung
{
    /// <summary>The parts Samsung firmware is split into, each its own .tar.md5.</summary>
    public enum FirmwarePart
    {
        AP,
        BL,
        CP,
        CSC,
        HomeCsc,
        Userdata,
    }

    /// <summary>
    /// A Samsung build number such as S918BXXU3BWK5: the model (S918B), the region (XX) or CSC
    /// (OXM), the update type (U, S), the bootloader binary (3), then the major version, year,
    /// month and build (B, W = 2023, K = November, 5). The binary is what decides a downgrade:
    /// a phone refuses a bootloader with a lower one ("SW REV CHECK FAIL").
    /// </summary>
    public sealed class SamsungBuild
    {
        public string Text { get; private set; }
        /// <summary>"S918B"; the phone's model is "SM-S918B".</summary>
        public string Model { get; private set; }
        /// <summary>The bootloader binary as a number: 0-9, then A = 10 … Z = 35.</summary>
        public int Binary { get; private set; }
        public char BinaryChar { get; private set; }
        /// <summary>Year and month the build was made, when the letters say so.</summary>
        public int Year { get; private set; }
        public int Month { get; private set; }

        static readonly Regex Shape = new Regex("^[A-Z0-9]{10,16}$");

        /// <summary>Null when <paramref name="text"/> is not a build number.</summary>
        public static SamsungBuild Parse(string text)
        {
            if (text == null)
                return null;
            text = text.Trim().ToUpperInvariant();
            if (!Shape.IsMatch(text))
                return null;
            int n = text.Length;
            char binary = text[n - 5];
            int value = BinaryValue(binary);
            int month = text[n - 2] - 'A' + 1;
            int year = text[n - 3] - 'A' + 2001;
            if (value < 0 || month < 1 || month > 12 || year < 2001 || year > 2030)
                return null;
            // Region "XX" (2 letters) and update type (1) in an AP build; CSC "OXM" (3) in a CSC build.
            string model = text.Substring(0, n - 8);
            if (model.Length < 4)
                return null;
            return new SamsungBuild { Text = text, Model = model, Binary = value, BinaryChar = binary, Year = year, Month = month };
        }

        /// <summary>The model of a CSC build (S918BOXM3BWK5), whose CSC code is one letter longer than a region.</summary>
        public static string ModelOfCsc(string cscBuild, string cscCode)
        {
            if (cscBuild == null || cscCode == null)
                return null;
            int at = cscBuild.ToUpperInvariant().IndexOf(cscCode.ToUpperInvariant(), StringComparison.Ordinal);
            return at > 3 ? cscBuild.Substring(0, at).ToUpperInvariant() : null;
        }

        public static int BinaryValue(char c)
        {
            c = char.ToUpperInvariant(c);
            if (c >= '0' && c <= '9')
                return c - '0';
            if (c >= 'A' && c <= 'Z')
                return c - 'A' + 10;
            return -1;
        }

        public override string ToString() => Text;
    }

    /// <summary>One firmware file, and what its name says.</summary>
    public sealed class FirmwareFile
    {
        public string Path { get; internal set; }
        public string FileName => BaseName(Path);

        /// <summary>The name after the last / or \, whichever system the path comes from.</summary>
        static string BaseName(string path)
        {
            path = path ?? "";
            int cut = Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\'));
            return path.Substring(cut + 1);
        }
        public FirmwarePart Part { get; internal set; }
        /// <summary>The build in the name (for CSC files, the CSC build).</summary>
        public string Build { get; internal set; }
        /// <summary>CSC and HOME_CSC: the CSC code, such as OXM or XSG.</summary>
        public string CscCode { get; internal set; }
        /// <summary>AP: the Android version from "_OS14", when the name has it.</summary>
        public int? Android { get; internal set; }

        static readonly Regex Name = new Regex(
            @"^(?<part>HOME_CSC|CSC|AP|BL|CP|USERDATA)_(?:(?<csc>[A-Z0-9]{3})_)?(?<build>[A-Z0-9]{10,16})(?:_|\.)",
            RegexOptions.IgnoreCase);
        static readonly Regex Os = new Regex(@"_OS(?<v>\d{1,2})(?:_|\.)", RegexOptions.IgnoreCase);

        /// <summary>Null when the name is not one of Samsung's.</summary>
        public static FirmwareFile FromPath(string path)
        {
            string name = BaseName(path);
            Match m = Name.Match(name);
            if (!m.Success)
                return null;
            string partText = m.Groups["part"].Value.ToUpperInvariant();
            FirmwarePart part = partText == "HOME_CSC" ? FirmwarePart.HomeCsc
                : partText == "USERDATA" ? FirmwarePart.Userdata
                : (FirmwarePart)Enum.Parse(typeof(FirmwarePart), partText);
            bool csc = part == FirmwarePart.CSC || part == FirmwarePart.HomeCsc;
            // A CSC name always has its code, and other parts never do.
            if (csc != m.Groups["csc"].Success)
                return null;
            FirmwareFile f = new FirmwareFile
            {
                Path = path,
                Part = part,
                Build = m.Groups["build"].Value.ToUpperInvariant(),
                CscCode = csc ? m.Groups["csc"].Value.ToUpperInvariant() : null,
            };
            Match os = Os.Match(name);
            if (os.Success)
                f.Android = int.Parse(os.Groups["v"].Value, CultureInfo.InvariantCulture);
            return f;
        }
    }

    /// <summary>
    /// The files of one firmware, as Samsung publishes them, and what they say together: the
    /// model, the bootloader binary, the Android version and the CSC.
    /// </summary>
    public sealed class FirmwareSet
    {
        public IReadOnlyList<FirmwareFile> Files { get; private set; }
        /// <summary>Files that are not Samsung firmware parts, by path.</summary>
        public IReadOnlyList<string> Unknown { get; private set; }

        public FirmwareFile Get(FirmwarePart part) => Files.FirstOrDefault(f => f.Part == part);

        /// <summary>The AP (or BL) build: model and bootloader binary.</summary>
        public SamsungBuild Build => SamsungBuild.Parse((Get(FirmwarePart.AP) ?? Get(FirmwarePart.BL) ?? Get(FirmwarePart.CP))?.Build);

        /// <summary>"SM-S918B", or null when no name says.</summary>
        public string Model
        {
            get
            {
                string model = Build?.Model;
                if (model == null)
                {
                    FirmwareFile csc = Get(FirmwarePart.CSC) ?? Get(FirmwarePart.HomeCsc);
                    model = SamsungBuild.ModelOfCsc(csc?.Build, csc?.CscCode);
                }
                return model == null ? null : "SM-" + model;
            }
        }

        public string CscCode => (Get(FirmwarePart.HomeCsc) ?? Get(FirmwarePart.CSC))?.CscCode;
        public int? Android => Get(FirmwarePart.AP)?.Android;

        /// <summary>
        /// Builds that disagree with each other (an AP of one firmware, a BL of another): each part
        /// and its build, when not all parts come from one model and bootloader binary.
        /// </summary>
        public IReadOnlyList<string> Mixed
        {
            get
            {
                List<string> mixed = new List<string>();
                SamsungBuild main = Build;
                if (main == null)
                    return mixed;
                foreach (FirmwareFile f in Files)
                {
                    if (f.Part == FirmwarePart.CSC || f.Part == FirmwarePart.HomeCsc)
                    {
                        string model = SamsungBuild.ModelOfCsc(f.Build, f.CscCode);
                        if (model != null && model != main.Model)
                            mixed.Add(f.FileName);
                        continue;
                    }
                    SamsungBuild b = SamsungBuild.Parse(f.Build);
                    // CP builds follow the modem and may differ in their last letters; the model counts.
                    if (b != null && (b.Model != main.Model || f.Part == FirmwarePart.BL && b.Binary != main.Binary))
                        mixed.Add(f.FileName);
                }
                return mixed;
            }
        }

        public static FirmwareSet FromPaths(IEnumerable<string> paths)
        {
            List<FirmwareFile> files = new List<FirmwareFile>();
            List<string> unknown = new List<string>();
            foreach (string path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                FirmwareFile f = FirmwareFile.FromPath(path);
                if (f == null)
                    unknown.Add(path);
                else if (files.All(x => x.Part != f.Part))
                    files.Add(f);
                else
                    unknown.Add(path); // a second AP, a second BL…
            }
            files.Sort((a, b) => a.Part.CompareTo(b.Part));
            return new FirmwareSet { Files = files, Unknown = unknown };
        }

        /// <summary>The Samsung firmware files in a folder (.tar.md5 and .tar).</summary>
        public static FirmwareSet FromFolder(string folder) =>
            FromPaths(Directory.EnumerateFiles(folder)
                .Where(p => p.EndsWith(".tar.md5", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".tar", StringComparison.OrdinalIgnoreCase))
                .Where(p => FirmwareFile.FromPath(p) != null));
    }
}
