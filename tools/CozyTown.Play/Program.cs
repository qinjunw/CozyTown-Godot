using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CozyTown.Core.Town;
using CozyTown.Game;
using CozyTown.Model;

namespace CozyTown.Play;

public static class PlayCommand
{
    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        return Run(args, Console.Out, Console.Error);
    }

    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        bool real = args.Contains("--real"), checkOnly = args.Contains("--check-only");
        BudgetSnapshot? budget = null;
        ModelBudgetProfile? profile = null;
        Mutex? instance = null;
        bool ownsInstance = false;
        try
        {
            if (args.SequenceEqual(new[] { "--help" }))
            {
                output.WriteLine("Usage: CozyTown.Play (--real|--offline) --settings=<local-settings.json> [--check-only]");
                return 0;
            }
            string[] settingsArguments = args.Where(a => a.StartsWith("--settings=", StringComparison.Ordinal)).ToArray();
            if (!OperatingSystem.IsWindows() || real == args.Contains("--offline") || settingsArguments.Length != 1
                || args.Distinct(StringComparer.Ordinal).Count() != args.Length
                || args.Any(a => a is not ("--real" or "--offline" or "--check-only") && a != settingsArguments[0]))
                throw new LaunchFailure("launcher_arguments_invalid");
            string settingsPath = Path.GetFullPath(settingsArguments[0][11..]);
            var settings = ReadJson<LauncherSettings>(settingsPath);
            string localRoot = Path.GetDirectoryName(settingsPath)!;
            string Resolve(string path) => string.IsNullOrWhiteSpace(path)
                ? throw new LaunchFailure("launcher_path_invalid") : Path.GetFullPath(path, localRoot);
            string executable = Resolve(settings.GameExecutable), manifestPath = Resolve(settings.PackageManifest);
            string modelConfig = Resolve(settings.ModelConfig), realData = Resolve(settings.RealDataDirectory);
            string offlineData = Resolve(settings.OfflineDataDirectory), packageRoot = Path.GetDirectoryName(executable)!;
            if (settings.ExecutionPurpose != TownModelRuntimeProfile.Manual) throw new LaunchFailure("execution_purpose_invalid");
            if (Overlaps(realData, offlineData) || Overlaps(realData, packageRoot) || Overlaps(offlineData, packageRoot)
                || new[] { settingsPath, manifestPath, modelConfig }.Any(p => Within(p, realData) || Within(p, offlineData)))
                throw new LaunchFailure("data_directories_overlap");
            foreach (string path in new[] { settingsPath, executable, manifestPath, modelConfig, realData, offlineData })
                RejectLinks(path);
            int packageFiles = VerifyPackage(settings, manifestPath, executable, packageRoot);

            if (real)
            {
                var local = ReadJson<TownModelRuntimeSettings>(modelConfig);
                var options = local.ToOptions();
                var runtime = TownModelRuntimeProfile.Resolve(options.BudgetProfileVersion, settings.ExecutionPurpose);
                profile = runtime.Profile;
                if (SamePath(options.CredentialFile, options.LedgerFile)
                    || new[] { options.CredentialFile, options.LedgerFile, options.LedgerFile + ".lock" }.Any(p =>
                        Within(p, realData) || Within(p, offlineData) || Within(p, packageRoot)))
                    throw new LaunchFailure("private_files_path_conflict");
                RejectLinks(options.CredentialFile);
                RejectLinks(options.LedgerFile);
                RejectLinks(options.LedgerFile + ".lock");
                _ = CredentialReader.Read(options.CredentialFile);
                budget = PersistentBudgetLedger.OpenExisting(options.LedgerFile, profile.Version).Snapshot();
                if (budget.StopReason is not null) throw new LaunchFailure("ledger_stopped");
                if (budget.TotalAttempts >= profile.TotalLimit) throw new LaunchFailure("total_limit");
                if (budget.Attempts.Count(a => a.Stage == TownModelRuntimeProfile.Manual) >= profile.StageLimits[TownModelRuntimeProfile.Manual])
                    throw new LaunchFailure("stage_limit");
                if (budget.SlotsHeld >= profile.ConcurrentLimit) throw new LaunchFailure("concurrency_limit");
                if (budget.AvailableMicros < profile.ReserveMicros) throw new LaunchFailure("money_limit");
            }
            string data = real ? realData : offlineData;
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = packageRoot };
            start.ArgumentList.Add("--");
            if (real)
            {
                start.ArgumentList.Add("--model-config=" + modelConfig);
                start.ArgumentList.Add("--execution-purpose=" + settings.ExecutionPurpose);
            }
            start.ArgumentList.Add("--data-dir=" + data);
            if (checkOnly)
            {
                output.WriteLine(TownJson.Serialize(new
                {
                    passed = true, code = "offline_precheck_passed", checkOnly = true, mode = real ? "real" : "offline",
                    sourceCommit = settings.ExpectedSourceCommit, packageFiles, budget = BudgetReport(budget, profile),
                    commandArguments = start.ArgumentList.Select(a => a.StartsWith("--model-config=", StringComparison.Ordinal)
                        ? "--model-config=<local-config>" : a.StartsWith("--data-dir=", StringComparison.Ordinal) ? "--data-dir=<separate-data>" : a),
                    gameStarted = false, realModelRequestsSent = 0, ledgerWritten = false
                }));
                return 0;
            }
            instance = new Mutex(false, "Local\\CozyTown.Godot.Play." + (real ? "real" : "offline"));
            try { ownsInstance = instance.WaitOne(0); }
            catch (AbandonedMutexException) { ownsInstance = true; }
            if (!ownsInstance) throw new LaunchFailure("launcher_already_running");
            output.WriteLine(real ? "榆溪小镇 · 真实模型入口（离线预检完成，尚未验证服务端调用）" : "榆溪小镇 · 离线规则入口");
            if (budget is not null) output.WriteLine(TownJson.Serialize(new { budget = BudgetReport(budget, profile) }));
            Directory.CreateDirectory(data);
            using var game = Process.Start(start) ?? throw new LaunchFailure("game_start_failed");
            game.WaitForExit();
            if (game.ExitCode != 0) throw new LaunchFailure("game_exited_with_error");
            return 0;
        }
        catch (Exception failure)
        {
            string code = failure switch { LaunchFailure launch => launch.Code, ModelException model => SafeCode(model.Code),
                JsonException => "launcher_json_invalid", InvalidDataException => "model_configuration_invalid", _ => "local_setup_unavailable" };
            error.WriteLine(code == "ledger_stopped" && budget?.StopReason == "fingerprint_not_registered"
                ? "共享账本先前因服务端指纹变化停止派发。原停止状态和未知金额预留保持不变。"
                : Message(code));
            output.WriteLine(TownJson.Serialize(new { passed = false, code, checkOnly, mode = real ? "real" : "offline",
                budget = BudgetReport(budget, profile) }));
            return 2;
        }
        finally
        {
            if (ownsInstance) instance!.ReleaseMutex();
            instance?.Dispose();
        }
    }

    private static object? BudgetReport(BudgetSnapshot? budget, ModelBudgetProfile? profile) => budget is null ? null : new
    {
        profile = profile!.Version, budget.BudgetId, currency = budget.Currency, totalUsed = budget.TotalAttempts, totalLimit = profile.TotalLimit,
        manualUsed = budget.Attempts.Count(a => a.Stage == TownModelRuntimeProfile.Manual),
        manualLimit = profile.StageLimits[TownModelRuntimeProfile.Manual], budget.SettledMicros, budget.ReservedMicros,
        budget.AvailableMicros, budget.SlotsHeld, concurrentLimit = profile.ConcurrentLimit, reservePerRequestMicros = profile.ReserveMicros,
        stopReason = budget.StopReason is null ? null : SafeCode(budget.StopReason)
    };

    private static T ReadJson<T>(string path)
    {
        using var input = File.OpenRead(path);
        if (input.Length > 1024 * 1024) throw new LaunchFailure("launcher_json_too_large");
        using var document = JsonDocument.Parse(input);
        void Unique(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var property in value.EnumerateObject())
                {
                    if (!names.Add(property.Name)) throw new LaunchFailure("launcher_json_invalid");
                    Unique(property.Value);
                }
            }
            else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) Unique(item);
        }
        Unique(document.RootElement);
        return TownJson.Deserialize<T>(document.RootElement.GetRawText());
    }

    private static int VerifyPackage(LauncherSettings settings, string manifestPath, string executable, string packageRoot)
    {
        if (!Regex.IsMatch(settings.ExpectedSourceCommit ?? "", "^[a-fA-F0-9]{40}$")
            || !Regex.IsMatch(settings.PackageManifestSha256 ?? "", "^[a-fA-F0-9]{64}$")
            || !Hash(manifestPath).Equals(settings.PackageManifestSha256, StringComparison.OrdinalIgnoreCase))
            throw new LaunchFailure("package_identity_invalid");
        var package = ReadJson<PackageFiles>(manifestPath);
        if (!string.Equals(package.SourceCommit, settings.ExpectedSourceCommit, StringComparison.OrdinalIgnoreCase)
            || package.Files is null || package.Files.Length == 0) throw new LaunchFailure("package_identity_invalid");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in package.Files)
        {
            if (file is null || string.IsNullOrWhiteSpace(file.Path) || Path.IsPathRooted(file.Path)
                || file.Path.Contains(':') || !Regex.IsMatch(file.Sha256 ?? "", "^[a-fA-F0-9]{64}$") || file.Bytes < 0)
                throw new LaunchFailure("package_manifest_invalid");
            string path = Path.GetFullPath(file.Path, packageRoot);
            if (!Within(path, packageRoot) || !names.Add(path)) throw new LaunchFailure("package_manifest_invalid");
            RejectLinks(path);
            if (!File.Exists(path) || new FileInfo(path).Length != file.Bytes
                || !Hash(path).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new LaunchFailure("package_file_missing_or_changed");
        }
        if (!names.Contains(executable)) throw new LaunchFailure("package_manifest_incomplete");
        var directories = new Stack<string>();
        directories.Push(packageRoot);
        while (directories.TryPop(out string? directory))
            foreach (string path in Directory.EnumerateFileSystemEntries(directory))
            {
                RejectLinks(path);
                if (Directory.Exists(path)) directories.Push(path);
                else if (!names.Contains(path)) throw new LaunchFailure("package_manifest_incomplete");
            }
        return names.Count;
    }

    private static string Hash(string path) { using var input = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(input)); }
    private static bool SamePath(string a, string b) => Path.GetFullPath(a).Equals(Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
    private static bool Within(string path, string directory)
    {
        string root = Path.GetFullPath(directory);
        string prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        return SamePath(path, directory) || Path.GetFullPath(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
    private static bool Overlaps(string a, string b) => Within(a, b) || Within(b, a);
    private static void RejectLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new LaunchFailure("launcher_link_path_invalid");
    }
    private static string SafeCode(string value) => Regex.IsMatch(value, "^[a-z0-9_]{1,80}$") ? value : "unrecognized_failure_code";
    private static string Message(string code) => code switch
    {
        "ledger_stopped" => "共享账本已停止派发；请查看 stopReason。原停止状态和金额预留保持不变。",
        "total_limit" => "共享总次数已到上限。请使用离线入口。",
        "stage_limit" => "人工试玩次数已到上限；NPC、玩家、失败和恢复共用此额度。请使用离线入口。",
        "money_limit" => "可用金额不足以预留下一次请求；预留金额不等于确认扣费。请使用离线入口。",
        "concurrency_limit" or "ledger_busy" => "共享槽位或账本正在使用。请等待现有请求结束。",
        "launcher_already_running" => "同模式入口已经打开。请回到已有游戏窗口。",
        "package_identity_invalid" or "package_file_missing_or_changed" or "package_manifest_incomplete" or "package_manifest_invalid"
            => "运行包与指定候选清单不一致。请核对候选版本并恢复完整运行文件。",
        "game_start_failed" or "game_exited_with_error" => "游戏未正常运行。请反馈操作和错误代码。",
        _ when code.StartsWith("credential_", StringComparison.Ordinal) => "本机凭据无法读取或格式不符合要求。请检查凭据引用；不要发送密钥。",
        "launcher_json_invalid" or "launcher_json_too_large" or "model_configuration_invalid" or "budget_profile_not_registered"
            or "execution_purpose_invalid" => "启动配置或模型预算配置无效。请检查本机配置字段和已登记用途。",
        _ => "本机启动检查未通过。请核对本机配置并反馈错误代码；不要发送密钥或私人文件内容。"
    };
    private sealed record LauncherSettings(string GameExecutable, string ModelConfig, string ExecutionPurpose,
        string RealDataDirectory, string OfflineDataDirectory, string PackageManifest, string ExpectedSourceCommit, string PackageManifestSha256);
    private sealed record PackageFiles(string SourceCommit, PackageFile[] Files);
    private sealed record PackageFile(string Path, long Bytes, string Sha256);
    private sealed class LaunchFailure(string code) : Exception(code) { public string Code { get; } = code; }
}
