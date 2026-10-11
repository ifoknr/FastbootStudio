using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using FastbootEnhance.Core.Adb;

namespace FastbootEnhance
{
    /// <summary>
    /// The Phone page: a phone running Android, read over adb while the page is open. The
    /// properties, kernel, battery and the Companion's backups on /sdcard need no root; with the
    /// Fastboot Studio Companion module (through su) the page also shows the bootloader's own AVB
    /// state, the root manager, storage wear and the conflicts between root modules. Backups can
    /// be copied to the PC and checked against their SHA256SUMS.
    /// </summary>
    static class PhoneUI
    {
        static MainWindow W => MainWindow.THIS;

        const string CompanionReleases = "https://github.com/ifoknr/FastbootStudio-Companion/releases/latest";

        // Long enough to tap "Allow" when the root manager asks; a refusal comes back at once.
        static readonly TimeSpan CompanionTimeout = TimeSpan.FromSeconds(20);
        static readonly TimeSpan CopyTimeout = TimeSpan.FromHours(3);

        static readonly ObservableCollection<PhoneBackupRow> backupRows = new ObservableCollection<PhoneBackupRow>();
        static readonly AutoResetEvent wake = new AutoResetEvent(false);

        // What a backup copied in this run of the app turned out to be, by folder name.
        static readonly Dictionary<string, KeyValuePair<string, string>> checkedCopies = new Dictionary<string, KeyValuePair<string, string>>();

        static volatile bool pageVisible;
        static volatile bool readAgain;
        static int copying;
        static string shownSerial;
        static string shownModel;

        static void log(string line) => LogStore.Append("phone: " + line);

        static void ui(Action action) => W.Dispatcher.Invoke(action);

        static string backupsOnPc => Path.Combine(BackupUI.SaveRoot, "Backups");

        public static void init()
        {
            W.phone_backups.ItemsSource = backupRows;
            W.phone_backups.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(rowButton));

            W.phone_reboot_recovery.Click += delegate { reboot("recovery", Properties.Resources.phone_reboot_recovery); };
            W.phone_reboot_fastbootd.Click += delegate { reboot("fastboot", "fastbootd"); };
            W.phone_reboot_bootloader.Click += delegate { reboot("bootloader", Properties.Resources.phone_bootloader); };
            W.phone_read_again.Click += delegate { readNow(); };
            W.phone_companion_retry.Click += delegate { readNow(); };
            W.phone_companion_get.Click += delegate { openLink(CompanionReleases); };
            W.phone_backups_open.Click += delegate { BackupUI.openInExplorer(backupsOnPc); };

            W.main_tabs.SelectionChanged += delegate (object sender, SelectionChangedEventArgs e)
            {
                if (e.OriginalSource != W.main_tabs)
                    return;
                pageVisible = W.main_tabs.SelectedItem == W.phone_tab;
                if (pageVisible)
                    wake.Set();
            };

            Thread poller = new Thread(poll) { IsBackground = true, Name = "phone poller" };
            poller.Start();
        }

        static void readNow()
        {
            readAgain = true;
            wake.Set();
        }

        // ---------------------------------------------------------------- reading the phone

        static void poll()
        {
            while (true)
            {
                wake.WaitOne(TimeSpan.FromSeconds(2.5));
                if (!pageVisible || Volatile.Read(ref copying) > 0)
                    continue;

                List<AdbDevice> devices;
                try
                {
                    Adb.Result list = Adb.Run("devices -l", Adb.ShortCommand);
                    devices = AdbDevice.ParseList(list.Output);
                }
                catch (FileNotFoundException e)
                {
                    log("adb.exe is missing: " + e.FileName);
                    ui(delegate { showEmpty(Properties.Resources.phone_empty_title, e.Message); });
                    return;
                }
                catch (Exception e)
                {
                    log("adb poll failed: " + e.Message);
                    continue;
                }

                AdbDevice phone = devices.FirstOrDefault(d => d.State == "device");
                if (phone == null)
                {
                    AdbDevice waiting = devices.FirstOrDefault(d => d.Unauthorized);
                    shownSerial = null;
                    ui(delegate
                    {
                        if (waiting != null)
                            showEmpty(Properties.Resources.phone_empty_title, string.Format(Properties.Resources.phone_unauthorized, waiting.Serial));
                        else
                            showEmpty(Properties.Resources.phone_empty_title, Properties.Resources.phone_empty_text);
                    });
                    continue;
                }

                if (phone.Serial == shownSerial && !readAgain)
                    continue;
                readAgain = false;
                read(phone);
            }
        }

        /// <summary>Reads the phone, without root first and then through the Companion. Blocks.</summary>
        static void read(AdbDevice phone)
        {
            string serial = phone.Serial;
            bool samePhone = serial == shownSerial;
            shownSerial = serial;
            shownModel = phone.Model ?? serial;
            if (!samePhone)
                ui(delegate { showEmpty(string.Format(Properties.Resources.phone_reading, shownModel), ""); });

            Adb.Result basic = Adb.Run(AdbCommand.Shell(serial, PhoneReport.Command), Adb.ShortCommand);
            if (!basic.Succeeded && string.IsNullOrWhiteSpace(basic.Output))
            {
                log(serial + " not read: " + basic.Problem);
                shownSerial = null;
                return;
            }
            PhoneReport report = PhoneReport.Parse(basic.Output);
            ui(delegate { show(serial, report, true); });

            // The Companion lives under /data/adb, which only root can reach. Ask su only when the
            // phone has one; Magisk shows a prompt the first time, KernelSU and APatch answer from
            // their own allow list.
            Adb.Result su = Adb.Run(AdbCommand.Shell(serial, "command -v su"), Adb.ShortCommand);
            bool hasSu = su.Succeeded && su.Output.Trim().Length > 0;
            string output = null;
            if (hasSu)
            {
                Adb.Result companion = Adb.Run(AdbCommand.Shell(serial,
                    AdbCommand.AsRoot(PhoneReport.CompanionCommand, RootAccess.Su)), CompanionTimeout);
                output = companion.Output;
            }
            report.ApplyCompanion(hasSu, output);
            log(serial + " (" + (report.Model ?? "?") + "): Android " + (report.Android ?? "?") + ", kernel " + (report.Kernel ?? "?")
                + ", " + report.Backups.Count + " backups, Companion " + report.Companion
                + (report.Companion == CompanionState.Ready ? " " + report.CompanionVersion + ", " + report.Conflicts.Count + " conflicts" : ""));
            ui(delegate
            {
                if (shownSerial == serial)
                    show(serial, report, false);
            });

            // The boot image check on the Device page uses the same phone's kernel and patch level.
            AdbFacts.ReadOnce(serial);
        }

        // ---------------------------------------------------------------- showing it

        static void showEmpty(string title, string text)
        {
            W.phone_page.Visibility = Visibility.Collapsed;
            W.phone_header_actions.Visibility = Visibility.Collapsed;
            W.phone_empty.Visibility = Visibility.Visible;
            W.phone_empty_title.Text = title;
            W.phone_empty_text.Text = text;
            W.phone_empty_text.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        }

        static void show(string serial, PhoneReport phone, bool companionPending)
        {
            W.phone_empty.Visibility = Visibility.Collapsed;
            W.phone_page.Visibility = Visibility.Visible;
            W.phone_header_actions.Visibility = Visibility.Visible;
            W.phone_serial.Text = serial;

            showDevice(phone);
            showSecurity(phone, companionPending);
            showCompanion(phone, companionPending);
            showBackups(phone);
        }

        static void showDevice(PhoneReport phone)
        {
            List<InfoRow> rows = new List<InfoRow>();
            string model = phone.Model;
            if (model != null && phone.Brand != null && model.IndexOf(phone.Brand, StringComparison.OrdinalIgnoreCase) < 0)
                model = phone.Brand + " " + model;
            add(rows, Properties.Resources.phone_model, model);
            add(rows, Properties.Resources.phone_chip, join(phone.Platform, phone.Arch));
            add(rows, Properties.Resources.phone_android, phone.Android == null ? null
                : phone.Android + (phone.Sdk != null ? " (SDK " + phone.Sdk + ")" : ""));
            add(rows, Properties.Resources.phone_patch, phone.Patch);
            add(rows, Properties.Resources.phone_slot, join(
                phone.Slot != null ? phone.Slot + " · A/B" : Properties.Resources.phone_slot_none,
                phone.Dynamic == true ? Properties.Resources.phone_dynamic : null));

            List<string> battery = new List<string>();
            if (phone.BatteryHealth != null)
                battery.Add(string.Format(Properties.Resources.phone_battery_health, phone.BatteryHealth));
            if (phone.BatteryCycles != null)
                battery.Add(string.Format(Properties.Resources.phone_battery_cycles, phone.BatteryCycles));
            if (battery.Count == 0 && phone.BatteryLevel != null)
                battery.Add(string.Format(Properties.Resources.phone_battery_charge, phone.BatteryLevel));
            add(rows, Properties.Resources.phone_battery, battery.Count > 0 ? string.Join(" · ", battery) : null);

            if (phone.StorageWear != null)
            {
                string type = phone.StorageType == "ufs" ? "UFS" : phone.StorageType == "emmc" ? "eMMC" : phone.StorageType ?? "";
                int wear = phone.StorageWear.Value;
                string text = wear >= 11 ? string.Format(Properties.Resources.phone_storage_past, type)
                    : string.Format(Properties.Resources.phone_storage_wear, type, (wear - 1) * 10, wear * 10);
                if (phone.StorageEol >= 2)
                    text += " · " + Properties.Resources.phone_storage_spare;
                add(rows, Properties.Resources.phone_storage, text);
            }
            W.phone_device_rows.ItemsSource = rows;

            W.phone_soc.Text = phone.Soc ?? "";
            W.phone_soc_chip.Visibility = phone.Soc != null ? Visibility.Visible : Visibility.Collapsed;
        }

        static void showSecurity(PhoneReport phone, bool companionPending)
        {
            bool ready = phone.Companion == CompanionState.Ready;
            List<InfoRow> rows = new List<InfoRow>();
            add(rows, Properties.Resources.phone_kernel, phone.Kernel);
            add(rows, "KMI", phone.Kmi);
            add(rows, Properties.Resources.phone_bootloader, phone.DeviceState == "unlocked" ? Properties.Resources.phone_unlocked
                : phone.DeviceState == "locked" ? Properties.Resources.phone_locked : phone.DeviceState);
            add(rows, "SELinux", phone.Selinux);
            add(rows, Properties.Resources.phone_root, ready ? phone.Root ?? Properties.Resources.phone_root_none
                : phone.Companion == CompanionState.NoRoot && !companionPending ? Properties.Resources.phone_root_none
                : Properties.Resources.phone_root_unknown);
            W.phone_security_rows.ItemsSource = rows;

            if (phone.Avb != null)
            {
                bool green = phone.Avb == "green";
                W.phone_avb.Text = "AVB " + phone.Avb;
                W.phone_avb.Foreground = Palette.Get(green ? "Ok" : "Warn");
                W.phone_avb_chip.Background = Palette.Get(green ? "AccentSoft" : "WarnSoft");
                W.phone_avb_chip.BorderBrush = Palette.Get(green ? "AccentLine" : "WarnLine");
                W.phone_avb_chip.Visibility = Visibility.Visible;
            }
            else
            {
                W.phone_avb_chip.Visibility = Visibility.Collapsed;
            }

            if (ready && phone.Spoofed)
            {
                W.phone_avb_note.Text = Properties.Resources.phone_spoofed;
                W.phone_avb_note.Foreground = Palette.Get("Warn");
                W.phone_avb_note.Visibility = Visibility.Visible;
            }
            else if (!ready && phone.Avb != null)
            {
                W.phone_avb_note.Text = Properties.Resources.phone_avb_from_props;
                W.phone_avb_note.Foreground = Palette.Get("Dim");
                W.phone_avb_note.Visibility = Visibility.Visible;
            }
            else
            {
                W.phone_avb_note.Visibility = Visibility.Collapsed;
            }
        }

        static void showCompanion(PhoneReport phone, bool pending)
        {
            List<ConflictRow> conflicts = new List<ConflictRow>();
            string text;
            string dot = "Faint";
            bool get = false, retry = false;

            if (pending)
            {
                text = Properties.Resources.phone_companion_reading;
            }
            else
            {
                switch (phone.Companion)
                {
                    case CompanionState.Ready:
                        if (phone.Conflicts.Count == 0)
                        {
                            text = string.Format(Properties.Resources.phone_conflicts_none, phone.ModuleCount ?? 0);
                            dot = "Ok";
                        }
                        else
                        {
                            text = string.Format(Properties.Resources.phone_conflicts_some, phone.Conflicts.Count);
                            dot = "Warn";
                        }
                        conflicts.AddRange(phone.Conflicts.Select(describe));
                        break;
                    case CompanionState.NotInstalled:
                        text = Properties.Resources.phone_companion_missing;
                        get = true;
                        break;
                    case CompanionState.Refused:
                        text = Properties.Resources.phone_companion_refused;
                        dot = "Warn";
                        retry = true;
                        break;
                    default:
                        text = Properties.Resources.phone_companion_no_root;
                        break;
                }
            }

            W.phone_companion_text.Text = text;
            W.phone_companion_dot.Fill = Palette.Get(dot);
            W.phone_companion_get.Visibility = get ? Visibility.Visible : Visibility.Collapsed;
            W.phone_companion_retry.Visibility = retry ? Visibility.Visible : Visibility.Collapsed;
            W.phone_conflicts.ItemsSource = conflicts;

            bool ready = !pending && phone.Companion == CompanionState.Ready && phone.CompanionVersion != null;
            W.phone_companion_version.Text = ready
                ? "v" + phone.CompanionVersion + (phone.CompanionChannel == "beta" ? " " + Properties.Resources.phone_companion_beta : "")
                : "";
            W.phone_companion_chip.Visibility = ready ? Visibility.Visible : Visibility.Collapsed;
        }

        static ConflictRow describe(ModuleConflict conflict)
        {
            string modules = string.Join(Properties.Resources.phone_and, conflict.Modules);
            switch (conflict.Kind)
            {
                case ConflictKind.File:
                    return new ConflictRow(conflict.Count > 1
                            ? string.Format(Properties.Resources.phone_conflict_files, modules, conflict.Count)
                            : string.Format(Properties.Resources.phone_conflict_file, modules),
                        string.Join("\n", conflict.Paths.Take(2)) + (conflict.Count > 2 ? "\n…" : ""), true);
                case ConflictKind.Replace:
                    return new ConflictRow(string.Format(Properties.Resources.phone_conflict_replace, conflict.By, modules),
                        conflict.Paths.FirstOrDefault() ?? "", true);
                default:
                    return new ConflictRow(string.Format(Properties.Resources.phone_conflict_prop, modules),
                        conflict.Key + " = " + string.Join(" / ", conflict.Values.Select(v => v.Value).Distinct()), false);
            }
        }

        static void showBackups(PhoneReport phone)
        {
            backupRows.Clear();
            foreach (PhoneBackup backup in phone.Backups)
            {
                string type, brush;
                switch (backup.Set)
                {
                    case BackupSet.Boot: type = Properties.Resources.phone_set_boot; brush = "Accent"; break;
                    case BackupSet.Critical: type = Properties.Resources.phone_set_critical; brush = "Warn"; break;
                    default: type = Properties.Resources.phone_set_full; brush = "Ok"; break;
                }
                PhoneBackupRow row = new PhoneBackupRow(backup, type, Palette.Get(brush),
                    backup.Kb != null ? Helper.byte2AUnit(backup.Kb.Value * 1024) : "",
                    backup.Taken?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "");
                showOnPc(row);
                backupRows.Add(row);
            }

            W.phone_backups_summary.Text = string.Format(Properties.Resources.phone_backups_summary,
                phone.Backups.Count, Helper.byte2AUnit(phone.BackupsKb * 1024));
            DateTime? last = phone.Backups.Select(b => b.Taken).FirstOrDefault(t => t != null);
            W.phone_backups_last.Text = last != null
                ? string.Format(Properties.Resources.phone_backups_last, last.Value.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))
                : "";
            W.phone_backups_last_chip.Visibility = last != null ? Visibility.Visible : Visibility.Collapsed;
            W.phone_backups_empty.Visibility = phone.Backups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>Whether the backup is in the Backup page's folder on this PC, and what its check said.</summary>
        static void showOnPc(PhoneBackupRow row)
        {
            string local = Path.Combine(backupsOnPc, row.Name);
            bool there = Directory.Exists(local) && File.Exists(Path.Combine(local, Sha256Sums.FileName));
            row.Copied = there;
            KeyValuePair<string, string> result;
            lock (checkedCopies)
            {
                if (there && checkedCopies.TryGetValue(row.Name, out result))
                {
                    row.SetState(result.Key, result.Value);
                    return;
                }
            }
            row.SetState(there ? Properties.Resources.phone_pc_yes : Properties.Resources.phone_pc_no, there ? "Ok" : "Dim");
        }

        // ---------------------------------------------------------------- actions

        static void rowButton(object sender, RoutedEventArgs e)
        {
            PhoneBackupRow row = (e.OriginalSource as Button)?.Tag as PhoneBackupRow;
            if (row == null)
                return;
            e.Handled = true;
            if (row.Copied)
                BackupUI.openInExplorer(Path.Combine(backupsOnPc, row.Name));
            else
                copy(row);
        }

        /// <summary>adb pull of the backup folder (it is on /sdcard: no root), then every file checked against SHA256SUMS.</summary>
        static void copy(PhoneBackupRow row)
        {
            string serial = shownSerial;
            if (serial == null)
                return;
            string target = backupsOnPc;
            string local = Path.Combine(target, row.Name);
            row.Idle = false;
            row.SetState(Properties.Resources.phone_pc_copying, "Accent");
            Interlocked.Increment(ref copying);

            string state = null, brush = null;
            Helper.offloadAndRun(delegate
            {
                try
                {
                    Directory.CreateDirectory(target);
                    Adb.Result pulled = Adb.Run(AdbCommand.Pull(serial, row.Backup.Path, target), CopyTimeout);
                    if (!pulled.Succeeded || !Directory.Exists(local))
                    {
                        state = string.Format(Properties.Resources.phone_pc_failed, pulled.Problem);
                        brush = "Danger";
                        log("copy of " + row.Backup.Path + " failed: " + pulled.Problem);
                        return;
                    }
                    ui(delegate { row.SetState(Properties.Resources.phone_pc_checking, "Accent"); });
                    VerifyReport report = Sha256Sums.Verify(local);
                    if (report.AllGood)
                    {
                        state = Properties.Resources.phone_pc_ok;
                        brush = "Ok";
                    }
                    else
                    {
                        state = string.Format(Properties.Resources.phone_pc_bad, Math.Max(1, report.Bad.Count), report.Total);
                        brush = "Danger";
                    }
                    log("copied " + row.Backup.Path + " to " + local + ": " + report.Good.Count + " of " + report.Total + " files match"
                        + (report.Bad.Count > 0 ? " (" + string.Join(", ", report.Bad) + ")" : ""));
                }
                catch (Exception e)
                {
                    state = string.Format(Properties.Resources.phone_pc_failed, e.Message);
                    brush = "Danger";
                    log("copy of " + row.Backup.Path + " failed: " + e);
                }
            }, delegate
            {
                Interlocked.Decrement(ref copying);
                lock (checkedCopies)
                    checkedCopies[row.Name] = new KeyValuePair<string, string>(state, brush);
                row.Copied = Directory.Exists(local) && File.Exists(Path.Combine(local, Sha256Sums.FileName));
                row.SetState(state, brush);
                row.Idle = true;
            });
        }

        static void reboot(string target, string name)
        {
            string serial = shownSerial;
            if (serial == null)
                return;
            if (!Helper.confirm(string.Format(Properties.Resources.phone_reboot_confirm, shownModel ?? serial, name), Properties.Resources.nav_phone))
                return;
            string error = null;
            Helper.offloadAndRun(delegate
            {
                Adb.Result result = Adb.Run(AdbCommand.Reboot(serial, target), Adb.ShortCommand);
                log("adb reboot " + target + " (" + serial + ") -> " + result.ExitCode);
                if (!result.Succeeded)
                    error = result.Problem;
            }, delegate
            {
                if (error != null)
                    ThemedDialog.Show(string.Format(Properties.Resources.phone_reboot_failed, error), Properties.Resources.nav_phone,
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                wake.Set();
            });
        }

        static void openLink(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception e)
            {
                log("cannot open " + url + ": " + e.Message);
            }
        }

        static void add(List<InfoRow> rows, string name, string value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                rows.Add(new InfoRow(name, value));
        }

        static string join(params string[] parts)
        {
            string[] present = parts.Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();
            return present.Length > 0 ? string.Join(" · ", present) : null;
        }
    }
}
