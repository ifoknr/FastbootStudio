using System.Collections.Generic;
using System.Linq;

namespace FastbootEnhance
{
    /// <summary>
    /// What the app is doing right now, in one place. Every page says when a job of its own
    /// starts and ends; closing the window, the restarts in Settings, the update prompt and
    /// the pages that would start a second fastboot all ask here instead of each knowing the
    /// other pages' flags. Safe from any thread.
    /// </summary>
    static class Busy
    {
        public enum Job
        {
            /// <summary>The Device and Flash pages: a payload flash or one fastboot command.</summary>
            Flash,

            /// <summary>The Root page while fastboot flashes or boots the patched image.</summary>
            RootFlash,

            /// <summary>The Root page reading an image or moving it over adb.</summary>
            Root,

            /// <summary>A Samsung phone in Download mode being read or flashed.</summary>
            Odin,

            /// <summary>A fastboot command typed in the Terminal.</summary>
            TerminalFastboot,

            /// <summary>An adb command typed in the Terminal.</summary>
            TerminalAdb,

            /// <summary>Partitions or files being copied from the phone, or a backup being checked.</summary>
            Backup,

            /// <summary>The Payload Dumper writing images.</summary>
            Payload,

            Images,
            Super,
            Samsung,
        }

        static readonly object gate = new object();
        static readonly HashSet<Job> running = new HashSet<Job>();

        public static void Set(Job job, bool on)
        {
            lock (gate)
            {
                if (on)
                    running.Add(job);
                else
                    running.Remove(job);
            }
        }

        public static bool Is(Job job)
        {
            lock (gate)
                return running.Contains(job);
        }

        /// <summary>Anything at all: no restart, and no update dialog popping up.</summary>
        public static bool Any
        {
            get
            {
                lock (gate)
                    return running.Count > 0;
            }
        }

        static readonly Job[] FastbootJobs = { Job.Flash, Job.RootFlash, Job.TerminalFastboot };

        /// <summary>
        /// The job that has fastboot talking to a phone, other than <paramref name="mine"/>; null
        /// when there is none. A second fastboot beside it could break a write.
        /// </summary>
        public static Job? Fastboot(Job? mine = null)
        {
            lock (gate)
            {
                foreach (Job job in FastbootJobs)
                {
                    if (job != mine && running.Contains(job))
                        return job;
                }
            }
            return null;
        }

        /// <summary>The page a job belongs to, as the rail names it.</summary>
        public static string Name(Job job)
        {
            switch (job)
            {
                case Job.Flash: return Properties.Resources.nav_flash;
                case Job.RootFlash:
                case Job.Root: return Properties.Resources.nav_root;
                case Job.Odin:
                case Job.Samsung: return Properties.Resources.nav_samsung;
                case Job.TerminalFastboot:
                case Job.TerminalAdb: return Properties.Resources.nav_terminal;
                case Job.Backup: return Properties.Resources.nav_backup;
                case Job.Payload: return Properties.Resources.payload_dumper;
                case Job.Images: return Properties.Resources.nav_images;
                default: return Properties.Resources.nav_super;
            }
        }

        /// <summary>The pages with a job running, for "wait for … to finish".</summary>
        public static string Names
        {
            get
            {
                List<Job> now;
                lock (gate)
                    now = running.ToList();
                return string.Join(", ", now.Select(Name).Distinct());
            }
        }
    }
}
