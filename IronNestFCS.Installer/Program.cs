using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("IronNestFCS 单文件安装器")]
[assembly: AssemblyDescription("铁巢重炮自动索敌 Mod 离线安装与卸载程序")]
[assembly: AssemblyCompany("IronNestFCS")]
[assembly: AssemblyProduct("IronNestFCS Installer")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("2026.8.24.0")]

namespace IronNestFCS.Installer
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            try
            {
                if (args.Length > 0 && string.Equals(args[0], "--verify", StringComparison.OrdinalIgnoreCase))
                {
                    InstallerEngine.VerifyPayload();
                    return 0;
                }

#if TEST_HOOKS
                if (args.Length >= 2 && string.Equals(args[0], "--test-install", StringComparison.OrdinalIgnoreCase))
                {
                    InstallerEngine.Install(args[1], delegate(string ignored) { });
                    return 0;
                }

                if (args.Length >= 2 && string.Equals(args[0], "--test-uninstall", StringComparison.OrdinalIgnoreCase))
                {
                    InstallerEngine.Uninstall(args[1], delegate(string ignored) { });
                    return 0;
                }
#endif

                if (!IsAdministrator())
                {
                    return RestartElevated();
                }

                Application.Run(new InstallerForm());
                return 0;
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "IronNestFCS 安装器", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }

        private static bool IsAdministrator()
        {
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
            {
                WindowsPrincipal principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
        }

        private static int RestartElevated()
        {
            try
            {
                ProcessStartInfo startInfo = new ProcessStartInfo();
                startInfo.FileName = Application.ExecutablePath;
                startInfo.UseShellExecute = true;
                startInfo.Verb = "runas";
                Process.Start(startInfo);
                return 0;
            }
            catch
            {
                MessageBox.Show("安装和卸载需要管理员权限，但权限请求已被取消。", "IronNestFCS 安装器", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return 1;
            }
        }
    }

    internal sealed class PayloadFile
    {
        internal readonly string ResourceName;
        internal readonly string RelativePath;
        internal readonly string Sha256;

        internal PayloadFile(string resourceName, string relativePath, string sha256)
        {
            ResourceName = resourceName;
            RelativePath = relativePath;
            Sha256 = sha256;
        }
    }

    internal static class InstallerEngine
    {
        private const string ProductPrefix = "Iron Nest Heavy Turret Simulator";

        private static readonly PayloadFile[] Payload = new PayloadFile[]
        {
            new PayloadFile("IronNestFCS.Payload.MelonLoader.x64.zip", "MelonLoader.x64.zip", "5B2B2F3D1CD42B59EC886C5BDC2663EDAE87A0097A4F4A8F58C0965A99DDA416"),
            new PayloadFile("IronNestFCS.Payload.UnityDependencies_6000.3.9.zip", "UnityDependencies_6000.3.9.zip", "C118313DBD7E882525620EE8D3ECB6CDDF3644044274642FF00CD334E6C59C1F"),
            new PayloadFile("IronNestFCS.Payload.UnityDependencies_6000.3.21.zip", "UnityDependencies_6000.3.21.zip", "A51C547EAB177EAC33C19356275123CB79EA189AF2F420885B1C1717D0406A60"),
            new PayloadFile("IronNestFCS.Payload.Cpp2IL.exe", "Cpp2IL.exe", "663FB432433B4371FD1EE0EBC321A8FFF2A9AAC5AC4230C843F9E03DDEE4E04C"),
            new PayloadFile("IronNestFCS.Payload.Cpp2IL.Plugin.StrippedCodeRegSupport.dll", "Cpp2IL.Plugin.StrippedCodeRegSupport.dll", "2CC4F8C66541F18B4DAED5410149EE7859C3190454D30BDA5CCD0895D2989EE6"),
            new PayloadFile("IronNestFCS.Payload.Mods.IronNestFCS.dll", "Mods\\IronNestFCS.dll", "68CAEDCBCA0D8AA92E515B073AC0C8D0AA7D13ABC3824BA33C32729C073888E9"),
            new PayloadFile("IronNestFCS.Payload.UserLibs.IronNestFCS.Abstractions.dll", "UserLibs\\IronNestFCS.Abstractions.dll", "454762F844AAEBF6FC4BDFD73570DDF6A88F14075E2FE73297B1C5C67CB1511D"),
            new PayloadFile("IronNestFCS.Payload.UserData.IronNestFCS.IronNestFCS.Logic.dll", "UserData\\IronNestFCS\\IronNestFCS.Logic.dll", "CE575A49051484F87EE1836FF35E660EEF0C1D4680A6A9C404C9FBE07E48CEE2")
        };

        internal static void VerifyPayload()
        {
            Assembly assembly = Assembly.GetExecutingAssembly();
            foreach (PayloadFile file in Payload)
            {
                using (Stream stream = OpenResource(assembly, file))
                {
                    string actual = ComputeSha256(stream);
                    if (!string.Equals(actual, file.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException("安装器内置文件校验失败：" + file.RelativePath + "。请重新下载安装器。");
                    }
                }
            }
        }

        internal static List<string> FindGameCandidates()
        {
            List<string> result = new List<string>();
            List<string> steamRoots = new List<string>();
            AddSteamRootFromRegistry(steamRoots, Registry.CurrentUser, @"Software\Valve\Steam");
            AddSteamRootFromRegistry(steamRoots, Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam");
            AddSteamRootFromRegistry(steamRoots, Registry.LocalMachine, @"SOFTWARE\Valve\Steam");

            string[] folderNames = new string[]
            {
                "Iron Nest Heavy Turret Simulator",
                "IRON NEST Heavy Turret Simulator Demo",
                "Iron Nest Heavy Turret Simulator Demo"
            };

            for (char drive = 'C'; drive <= 'G'; drive++)
            {
                string root = drive + @":\";
                if (!Directory.Exists(root))
                {
                    continue;
                }

                foreach (string folderName in folderNames)
                {
                    AddCandidate(result, Path.Combine(root, "Games", folderName));
                }

                AddUnique(steamRoots, Path.Combine(root, "SteamLibrary"));
                AddUnique(steamRoots, Path.Combine(root, "Steam"));
            }

            List<string> initialRoots = new List<string>(steamRoots);
            foreach (string steamRoot in initialRoots)
            {
                string libraryFile = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
                if (!File.Exists(libraryFile))
                {
                    continue;
                }

                foreach (string line in File.ReadAllLines(libraryFile))
                {
                    int pathKey = line.IndexOf("\"path\"", StringComparison.OrdinalIgnoreCase);
                    if (pathKey < 0)
                    {
                        continue;
                    }

                    int firstQuote = line.IndexOf('"', pathKey + 6);
                    int secondQuote = firstQuote < 0 ? -1 : line.IndexOf('"', firstQuote + 1);
                    if (firstQuote >= 0 && secondQuote > firstQuote)
                    {
                        AddUnique(steamRoots, line.Substring(firstQuote + 1, secondQuote - firstQuote - 1).Replace("\\\\", "\\"));
                    }
                }
            }

            foreach (string steamRoot in steamRoots)
            {
                foreach (string folderName in folderNames)
                {
                    AddCandidate(result, Path.Combine(steamRoot, "steamapps", "common", folderName));
                }
            }

            result.Sort(delegate(string left, string right)
            {
                bool leftDemo = left.IndexOf("Demo", StringComparison.OrdinalIgnoreCase) >= 0;
                bool rightDemo = right.IndexOf("Demo", StringComparison.OrdinalIgnoreCase) >= 0;
                if (leftDemo != rightDemo)
                {
                    return leftDemo ? 1 : -1;
                }
                return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
            });
            return result;
        }

        internal static string Install(string target, Action<string> status)
        {
            string gameRoot = ValidateGameRoot(target);
            EnsureGameStopped();

            status("正在校验单文件安装包……");
            VerifyPayload();

            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
            string backupRoot = Path.Combine(gameRoot, "IronNestFCS_Backups", stamp);
            List<string> log = new List<string>();
            log.Add("IronNestFCS 安装时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            log.Add("游戏目录：" + gameRoot);

            status("正在备份原文件……");
            string[] backupTargets = new string[]
            {
                "version.dll",
                @"Mods\IronNestFCS.dll",
                @"UserLibs\IronNestFCS.Abstractions.dll",
                @"UserData\IronNestFCS\IronNestFCS.Logic.dll",
                @"UserData\Loader.cfg"
            };
            foreach (string relativePath in backupTargets)
            {
                string existing = Path.Combine(gameRoot, relativePath);
                if (File.Exists(existing))
                {
                    string backupPath = Path.Combine(backupRoot, relativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(backupPath));
                    File.Copy(existing, backupPath, true);
                    log.Add("已备份：" + relativePath);
                }
            }

            string existingMelonLoader = Path.Combine(gameRoot, "MelonLoader");
            if (Directory.Exists(existingMelonLoader))
            {
                Directory.CreateDirectory(backupRoot);
                Directory.Move(existingMelonLoader, Path.Combine(backupRoot, "MelonLoader"));
                log.Add("已备份并移出旧 MelonLoader 文件夹。");
            }

            string tempRoot = Path.Combine(Path.GetTempPath(), "IronNestFCS-Installer-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRoot);
            try
            {
                status("正在安装 MelonLoader……");
                string melonZip = Path.Combine(tempRoot, "MelonLoader.x64.zip");
                WriteResourceToFile(FindPayload("MelonLoader.x64.zip"), melonZip);
                string extracted = Path.Combine(tempRoot, "MelonLoader");
                Directory.CreateDirectory(extracted);
                ZipFile.ExtractToDirectory(melonZip, extracted);
                CopyDirectoryContents(extracted, gameRoot);
            }
            finally
            {
                SafeDeleteTemporaryDirectory(tempRoot);
            }
            log.Add("已安装或修复 MelonLoader x64。");

            status("正在补齐离线依赖……");
            string generatorDir = Path.Combine(gameRoot, "MelonLoader", "Dependencies", "Il2CppAssemblyGenerator");
            string cppDir = Path.Combine(generatorDir, "Cpp2IL");
            string cppPluginDir = Path.Combine(cppDir, "Plugins");
            Directory.CreateDirectory(cppPluginDir);
            WriteResourceToFile(FindPayload("UnityDependencies_6000.3.9.zip"), Path.Combine(generatorDir, "UnityDependencies_6000.3.9.zip"));
            WriteResourceToFile(FindPayload("UnityDependencies_6000.3.21.zip"), Path.Combine(generatorDir, "UnityDependencies_6000.3.21.zip"));
            WriteResourceToFile(FindPayload("Cpp2IL.exe"), Path.Combine(cppDir, "Cpp2IL.exe"));
            WriteResourceToFile(FindPayload("Cpp2IL.Plugin.StrippedCodeRegSupport.dll"), Path.Combine(cppPluginDir, "Cpp2IL.Plugin.StrippedCodeRegSupport.dll"));
            log.Add("已安装 UnityDependencies 6000.3.9/6000.3.21、Cpp2IL 与配套 StrippedCodeRegSupport 插件。");

            ConfigureOfflineGeneration(gameRoot);
            log.Add("已开启 MelonLoader 强制离线生成，启动时不再连接 RemoteAPI。");

            status("正在安装自动索敌 Mod……");
            foreach (PayloadFile file in Payload)
            {
                if (!file.RelativePath.StartsWith("Mods\\", StringComparison.OrdinalIgnoreCase) &&
                    !file.RelativePath.StartsWith("UserLibs\\", StringComparison.OrdinalIgnoreCase) &&
                    !file.RelativePath.StartsWith("UserData\\", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                WriteResourceToFile(file, Path.Combine(gameRoot, file.RelativePath));
                log.Add("已安装：" + file.RelativePath);
            }

            string installedLog = Path.Combine(gameRoot, "UserData", "IronNestFCS", "Installer.log");
            log.Add("安装完成。离线依赖已写入游戏目录。");
            File.WriteAllLines(installedLog, log.ToArray(), new UTF8Encoding(true));
            return Directory.Exists(backupRoot) ? backupRoot : string.Empty;
        }

        internal static List<string> Uninstall(string target, Action<string> status)
        {
            string gameRoot = ValidateGameRoot(target);
            EnsureGameStopped();
            status("正在清除全部 Mod 相关文件……");

            string[] targets = new string[]
            {
                "version.dll",
                "MelonLoader",
                "Mods",
                "Plugins",
                "UserLibs",
                "UserData",
                "IronNestFCS_Backups"
            };
            List<string> removed = new List<string>();
            string rootPrefix = EnsureTrailingSeparator(gameRoot);
            foreach (string relativePath in targets)
            {
                string fullPath = Path.GetFullPath(Path.Combine(gameRoot, relativePath));
                if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("拒绝删除游戏目录之外的路径：" + fullPath);
                }

                if (File.Exists(fullPath))
                {
                    File.SetAttributes(fullPath, FileAttributes.Normal);
                    File.Delete(fullPath);
                    removed.Add(relativePath);
                }
                else if (Directory.Exists(fullPath))
                {
                    NormalizeAttributes(fullPath);
                    Directory.Delete(fullPath, true);
                    removed.Add(relativePath);
                }
            }
            return removed;
        }

        internal static bool IsGameDirectory(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path) || path.TrimEnd('\\', '/').EndsWith("_Data", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            try
            {
                foreach (string exe in Directory.GetFiles(path, "*.exe", SearchOption.TopDirectoryOnly))
                {
                    if (Path.GetFileNameWithoutExtension(exe).StartsWith(ProductPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
            catch
            {
                return false;
            }
            return false;
        }

        private static string ValidateGameRoot(string target)
        {
            if (string.IsNullOrWhiteSpace(target))
            {
                throw new InvalidOperationException("请先选择游戏目录。");
            }

            string gameRoot = Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!IsGameDirectory(gameRoot))
            {
                throw new InvalidOperationException("所选目录不是游戏根目录。请选择能够看到游戏 exe 的目录。");
            }
            return gameRoot;
        }

        private static void EnsureGameStopped()
        {
            foreach (Process process in Process.GetProcesses())
            {
                using (process)
                {
                    string processName;
                    try
                    {
                        processName = process.ProcessName;
                    }
                    catch
                    {
                        continue;
                    }

                    if (processName.StartsWith(ProductPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException("游戏正在运行。请先完全退出游戏，再重新操作。");
                    }
                }
            }
        }

        private static void AddSteamRootFromRegistry(List<string> roots, RegistryKey hive, string subKey)
        {
            try
            {
                using (RegistryKey key = hive.OpenSubKey(subKey))
                {
                    if (key == null)
                    {
                        return;
                    }
                    AddUnique(roots, Convert.ToString(key.GetValue("SteamPath"), CultureInfo.InvariantCulture));
                    AddUnique(roots, Convert.ToString(key.GetValue("InstallPath"), CultureInfo.InvariantCulture));
                }
            }
            catch
            {
            }
        }

        private static void AddUnique(List<string> paths, string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            {
                return;
            }

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(path);
            }
            catch
            {
                return;
            }

            foreach (string existing in paths)
            {
                if (string.Equals(existing, fullPath, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
            paths.Add(fullPath);
        }

        private static void AddCandidate(List<string> result, string path)
        {
            if (!IsGameDirectory(path))
            {
                return;
            }
            AddUnique(result, path);
        }

        private static PayloadFile FindPayload(string relativePath)
        {
            foreach (PayloadFile file in Payload)
            {
                if (string.Equals(file.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase))
                {
                    return file;
                }
            }
            throw new InvalidOperationException("找不到内置文件：" + relativePath);
        }

        private static Stream OpenResource(Assembly assembly, PayloadFile file)
        {
            Stream stream = assembly.GetManifestResourceStream(file.ResourceName);
            if (stream == null)
            {
                throw new InvalidDataException("安装器不完整：缺少内置文件 " + file.RelativePath + "。");
            }
            return stream;
        }

        private static string ComputeSha256(Stream stream)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(stream);
                StringBuilder text = new StringBuilder(hash.Length * 2);
                foreach (byte value in hash)
                {
                    text.Append(value.ToString("X2", CultureInfo.InvariantCulture));
                }
                return text.ToString();
            }
        }

        private static void WriteResourceToFile(PayloadFile file, string destination)
        {
            string parent = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            Assembly assembly = Assembly.GetExecutingAssembly();
            using (Stream input = OpenResource(assembly, file))
            using (FileStream output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                input.CopyTo(output);
            }
        }

        private static void ConfigureOfflineGeneration(string gameRoot)
        {
            string configPath = Path.Combine(gameRoot, "UserData", "Loader.cfg");
            Directory.CreateDirectory(Path.GetDirectoryName(configPath));

            string text = File.Exists(configPath) ? File.ReadAllText(configPath) : string.Empty;
            string newline = text.IndexOf("\r\n", StringComparison.Ordinal) >= 0 ? "\r\n" : Environment.NewLine;
            Regex setting = new Regex("^(?<indent>[ \\t]*)force_offline_generation[ \\t]*=[ \\t]*(?:true|false)[ \\t]*$", RegexOptions.IgnoreCase | RegexOptions.Multiline);
            if (setting.IsMatch(text))
            {
                text = setting.Replace(text, delegate(Match match)
                {
                    return match.Groups["indent"].Value + "force_offline_generation = true";
                }, 1);
            }
            else
            {
                Regex unitySection = new Regex("^\\[unityengine\\][ \\t]*$", RegexOptions.IgnoreCase | RegexOptions.Multiline);
                Match section = unitySection.Match(text);
                if (section.Success)
                {
                    int insertAt = section.Index + section.Length;
                    text = text.Insert(insertAt, newline + "force_offline_generation = true");
                }
                else
                {
                    if (text.Length > 0 && !text.EndsWith("\n", StringComparison.Ordinal))
                    {
                        text += newline;
                    }
                    text += "[unityengine]" + newline + "force_offline_generation = true" + newline;
                }
            }

            File.WriteAllText(configPath, text, new UTF8Encoding(false));
        }

        private static void CopyDirectoryContents(string sourceRoot, string destinationRoot)
        {
            foreach (string directory in Directory.GetDirectories(sourceRoot, "*", SearchOption.AllDirectories))
            {
                string relative = directory.Substring(sourceRoot.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                Directory.CreateDirectory(Path.Combine(destinationRoot, relative));
            }

            foreach (string file in Directory.GetFiles(sourceRoot, "*", SearchOption.AllDirectories))
            {
                string relative = file.Substring(sourceRoot.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string destination = Path.Combine(destinationRoot, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination));
                File.Copy(file, destination, true);
            }
        }

        private static void SafeDeleteTemporaryDirectory(string path)
        {
            string fullPath = Path.GetFullPath(path);
            string tempPrefix = EnsureTrailingSeparator(Path.GetFullPath(Path.GetTempPath()));
            if (!fullPath.StartsWith(tempPrefix, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(fullPath).StartsWith("IronNestFCS-Installer-", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("拒绝清理非安装器临时目录：" + fullPath);
            }

            if (Directory.Exists(fullPath))
            {
                NormalizeAttributes(fullPath);
                Directory.Delete(fullPath, true);
            }
        }

        private static void NormalizeAttributes(string directory)
        {
            foreach (string file in Directory.GetFiles(directory, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
        }

        private static string EnsureTrailingSeparator(string path)
        {
            return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        }
    }

    internal sealed class InstallerForm : Form
    {
        private readonly ComboBox pathBox;
        private readonly Button browseButton;
        private readonly Button installButton;
        private readonly Button uninstallButton;
        private readonly Label statusLabel;

        internal InstallerForm()
        {
            Text = "IronNestFCS 自动索敌单文件安装器";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(724, 287);
            MinimumSize = new Size(740, 326);
            MaximumSize = new Size(740, 326);
            MaximizeBox = false;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            Font = new Font("Microsoft YaHei UI", 10F);

            Label title = new Label();
            title.Text = "IronNestFCS 自动索敌 Mod";
            title.Font = new Font("Microsoft YaHei UI", 16F, FontStyle.Bold);
            title.AutoSize = true;
            title.Location = new Point(24, 20);
            Controls.Add(title);

            Label description = new Label();
            description.Text = "所需文件全部在本程序内，无需脚本或联网。支持 Unity 6000.3.9/6000.3.21。";
            description.AutoSize = true;
            description.Location = new Point(27, 60);
            Controls.Add(description);

            Label pathLabel = new Label();
            pathLabel.Text = "游戏目录：";
            pathLabel.AutoSize = true;
            pathLabel.Location = new Point(27, 103);
            Controls.Add(pathLabel);

            pathBox = new ComboBox();
            pathBox.Location = new Point(110, 98);
            pathBox.Size = new Size(492, 30);
            pathBox.DropDownStyle = ComboBoxStyle.DropDown;
            List<string> candidates = InstallerEngine.FindGameCandidates();
            foreach (string candidate in candidates)
            {
                pathBox.Items.Add(candidate);
            }
            if (pathBox.Items.Count > 0)
            {
                pathBox.SelectedIndex = 0;
            }
            Controls.Add(pathBox);

            browseButton = new Button();
            browseButton.Text = "浏览…";
            browseButton.Location = new Point(612, 96);
            browseButton.Size = new Size(86, 33);
            browseButton.Click += BrowseButtonClick;
            Controls.Add(browseButton);

            statusLabel = new Label();
            statusLabel.Text = candidates.Count > 0 ? "已检测到游戏，可以开始安装。" : "未自动找到游戏，请选择包含游戏 exe 的目录。";
            statusLabel.AutoSize = true;
            statusLabel.Location = new Point(27, 151);
            Controls.Add(statusLabel);

            uninstallButton = new Button();
            uninstallButton.Text = "完全卸载";
            uninstallButton.Location = new Point(361, 204);
            uninstallButton.Size = new Size(130, 43);
            uninstallButton.Click += UninstallButtonClick;
            Controls.Add(uninstallButton);

            installButton = new Button();
            installButton.Text = "开始安装";
            installButton.Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold);
            installButton.Location = new Point(508, 197);
            installButton.Size = new Size(190, 50);
            installButton.Click += InstallButtonClick;
            Controls.Add(installButton);

            Button cancelButton = new Button();
            cancelButton.Text = "取消";
            cancelButton.Location = new Point(250, 210);
            cancelButton.Size = new Size(94, 36);
            cancelButton.Click += delegate { Close(); };
            Controls.Add(cancelButton);
        }

        private void BrowseButtonClick(object sender, EventArgs e)
        {
            using (FolderBrowserDialog dialog = new FolderBrowserDialog())
            {
                dialog.Description = "选择包含游戏 exe 的根目录";
                if (Directory.Exists(pathBox.Text))
                {
                    dialog.SelectedPath = pathBox.Text;
                }
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    pathBox.Text = dialog.SelectedPath;
                }
            }
        }

        private void InstallButtonClick(object sender, EventArgs e)
        {
            RunOperation(delegate
            {
                string backup = InstallerEngine.Install(pathBox.Text, SetStatus);
                statusLabel.Text = "安装完成。现在可以启动游戏。";
                string message = "安装成功！\r\n\r\n离线依赖已安装，首次启动只需等待 MelonLoader 生成游戏程序集。";
                if (!string.IsNullOrEmpty(backup))
                {
                    message += "\r\n\r\n原文件备份位置：\r\n" + backup;
                }
                MessageBox.Show(this, message, "IronNestFCS 安装完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
            });
        }

        private void UninstallButtonClick(object sender, EventArgs e)
        {
            DialogResult confirmation = MessageBox.Show(
                this,
                "将永久删除：\r\n\r\nMelonLoader、version.dll、Mods、Plugins、UserLibs、UserData 和 IronNestFCS_Backups。\r\n\r\n其中的其他 Mod、配置、日志和备份也会一并删除，只保留游戏本体。删除后无法恢复。是否继续？",
                "确认完全卸载",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (confirmation != DialogResult.Yes)
            {
                statusLabel.Text = "已取消卸载。";
                return;
            }

            RunOperation(delegate
            {
                List<string> removed = InstallerEngine.Uninstall(pathBox.Text, SetStatus);
                statusLabel.Text = "Mod 清理完成。";
                string message = removed.Count > 0
                    ? "清理成功，共移除 " + removed.Count.ToString(CultureInfo.InvariantCulture) + " 项 Mod 相关内容。\r\n\r\n游戏本体已保留。"
                    : "未找到需要清理的 Mod 相关文件，游戏目录已是纯净状态。";
                MessageBox.Show(this, message, "IronNestFCS 卸载完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
            });
        }

        private void RunOperation(Action operation)
        {
            SetControlsEnabled(false);
            try
            {
                operation();
            }
            catch (Exception ex)
            {
                statusLabel.Text = "操作未完成。";
                MessageBox.Show(this, ex.Message, "IronNestFCS 安装器", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                SetControlsEnabled(true);
            }
        }

        private void SetStatus(string text)
        {
            statusLabel.Text = text;
            Application.DoEvents();
        }

        private void SetControlsEnabled(bool enabled)
        {
            pathBox.Enabled = enabled;
            browseButton.Enabled = enabled;
            installButton.Enabled = enabled;
            uninstallButton.Enabled = enabled;
        }
    }
}
