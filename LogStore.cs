using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace FastbootEnhance
{
    /// <summary>
    /// The session log. Everything fastboot prints, and what the app did about it, lands here;
    /// the Logs page and the Flash page's live log both listen to it. It replaces the old
    /// pop-up logger, which only recorded while its window happened to be open.
    /// While a flash runs (<see cref="BeginFile"/>), every line also goes to a file in the
    /// logs folder, so what happened to a phone can be read back after the app is closed.
    /// </summary>
    static class LogStore
    {
        const int MaxChars = 2000000;
        const int TrimTo = 1500000;

        /// <summary>Older flash logs are deleted past this many.</summary>
        const int KeepFiles = 30;

        static readonly object gate = new object();
        static readonly StringBuilder text = new StringBuilder();
        static StreamWriter file;

        /// <summary>Where the per-flash logs are kept: %APPDATA%\Fastboot Studio\logs.</summary>
        public static string Folder => Path.Combine(Settings.Folder, "logs");

        /// <summary>The file the current flash is written to; null between flashes.</summary>
        public static string CurrentFile { get; private set; }

        /// <summary>Raised on the writer's thread with the stamped line.</summary>
        public static event Action<string> LineAdded;

        /// <summary>Raised after <see cref="Clear"/>.</summary>
        public static event Action Cleared;

        public static void Append(string line)
        {
            if (line == null)
                return;

            string stamped = DateTime.Now.ToString("HH:mm:ss") + "  " + line;
            lock (gate)
            {
                text.Append(stamped).Append('\n');
                if (text.Length > MaxChars)
                    text.Remove(0, text.Length - TrimTo);
                if (file != null)
                {
                    try
                    {
                        file.WriteLine(stamped);
                    }
                    catch (IOException)
                    {
                        closeFile();
                    }
                }
            }

            Action<string> handler = LineAdded;
            if (handler != null)
                handler(stamped);
        }

        public static string Snapshot()
        {
            lock (gate)
                return text.ToString();
        }

        public static void Clear()
        {
            lock (gate)
                text.Clear();

            Action handler = Cleared;
            if (handler != null)
                handler();
        }

        /// <summary>
        /// Starts writing the log to "flash-&lt;date&gt;-&lt;what&gt;.log" in the logs folder as well,
        /// until <see cref="EndFile"/>. A folder that cannot be written is simply not written to.
        /// Returns the file's path, or null.
        /// </summary>
        public static string BeginFile(string what)
        {
            lock (gate)
            {
                closeFile();
                try
                {
                    Directory.CreateDirectory(Folder);
                    string safe = new string((what ?? "").Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());
                    string path = Path.Combine(Folder, "flash-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + (safe.Length > 0 ? "-" + safe : "") + ".log");
                    file = new StreamWriter(path, false, new UTF8Encoding(false)) { AutoFlush = true };
                    file.WriteLine("Fastboot Studio " + MainWindow.version + " · " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                    CurrentFile = path;
                    prune();
                    return path;
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    file = null;
                    CurrentFile = null;
                    return null;
                }
            }
        }

        /// <summary>Closes the current flash log file, if one is open.</summary>
        public static void EndFile()
        {
            lock (gate)
                closeFile();
        }

        static void closeFile()
        {
            if (file == null)
                return;
            try
            {
                file.Dispose();
            }
            catch (IOException)
            {
            }
            file = null;
            CurrentFile = null;
        }

        /// <summary>Keeps the newest <see cref="KeepFiles"/> flash logs.</summary>
        static void prune()
        {
            try
            {
                List<FileInfo> logs = new DirectoryInfo(Folder).GetFiles("flash-*.log").OrderByDescending(f => f.Name).ToList();
                foreach (FileInfo old in logs.Skip(KeepFiles))
                    old.Delete();
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
            }
        }
    }
}
