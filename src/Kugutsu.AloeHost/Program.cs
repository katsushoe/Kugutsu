using System.Runtime.CompilerServices;
using System.Text.Json;
using Aloe.CommonLib;
using Aloe.CompilerLib;
using Aloe.RuntimeLib;
using Aloe.CommonLib.Constants;
using Kugutsu.Contracts;
using Kugutsu.Hosting;

namespace Kugutsu.AloeHost;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, _) => Console.Error.WriteLine("Aloe helper failed.");
        TaskScheduler.UnobservedTaskException += (_, failure) => { failure.SetObserved(); Console.Error.WriteLine("Aloe helper task failed."); };
        if (args.Length != 1 || args[0] != "--worker") { return 2; }
        using var input = Console.OpenStandardInput();
        using var output = Console.OpenStandardOutput();
        try
        {
            var paths = InstallationPaths.FromExecutableDirectory(AppContext.BaseDirectory);
            using var assets = new AloeDependencies(paths).OpenVerified(CancellationToken.None);
            assets.LoadAssemblies();
            var request = await AloeWire.ReadAsync(input, CancellationToken.None);
            var source = request.GetProperty("source").GetString();
            if (request.GetProperty("command").GetString() != "compile" || !ValidSource(source))
            { await ErrorAsync(output, ErrorCode.INVALID_ARGUMENT); return 2; }
            object module;
            try { module = Compile(source!); }
            catch (Exception error) when (error.GetType().FullName is "Aloe.CompilerLib.AloeCompileException"
                or "Aloe.CompilerLib.Lexer.AloeLexerException")
            { await ErrorAsync(output, ErrorCode.ALOE_COMPILE_ERROR); return 1; }
            var counts = Counts(module);
            await AloeWire.WriteAsync(output, new { status = "compiled", instructionCount = counts.Instructions, functionCount = counts.Functions }, CancellationToken.None);
            var command = await AloeWire.ReadAsync(input, CancellationToken.None);
            if (command.GetProperty("command").GetString() == "close") { return 0; }
            if (command.GetProperty("command").GetString() != "run") { return 2; }
            try
            {
                var outcome = Run(module, paths, input, output, command.GetProperty("maximumInstructions").GetInt32());
                await AloeWire.WriteAsync(output, new { status = "completed", stage = "runtime", instructions = outcome.Instructions,
                    instructionPointer = outcome.Pointer }, CancellationToken.None);
                return 0;
            }
            catch (AloeExecutionFailure failure)
            {
                await AloeWire.WriteAsync(output, new { status = "error", stage = "runtime", code = failure.Code.ToString(),
                    instructionPointer = failure.Pointer, instructions = failure.Instructions }, CancellationToken.None);
                return 1;
            }
            catch (Exception)
            { await ErrorAsync(output, ErrorCode.ALOE_RUNTIME_ERROR, "runtime"); return 1; }
        }
        catch (AloeDependencyException) { await ErrorAsync(output, ErrorCode.ALOE_UNAVAILABLE); return 1; }
        catch (EndOfStreamException) { return 0; }
        catch (Exception) { Console.Error.WriteLine("Aloe helper failed."); await ErrorAsync(output, ErrorCode.INTERNAL_ERROR); return 1; }
    }

    internal static bool ValidSource(string? source) => source is { Length: > 0 and <= 65536 } && !source.Contains('\0')
        && ValidUnicode(source);
    private static bool ValidUnicode(string source)
    {
        for (var index = 0; index < source.Length; index++)
        {
            if (char.IsHighSurrogate(source[index]))
            { if (++index >= source.Length || !char.IsLowSurrogate(source[index])) { return false; } }
            else if (char.IsLowSurrogate(source[index])) { return false; }
        }
        return true;
    }
    private const string HostPrelude = "function execute_operation(name: string, args: string): string { return readLine(); }\n"
        + "function register_signal(conditionJson: string, retentionSeconds: string): string { return readLine(); }\n"
        + "function wait_for_signal(signalId: string, timeoutMs: string): string { return readLine(); }\n"
        + "function unregister_signal(signalId: string): string { return readLine(); }\n";
    [MethodImpl(MethodImplOptions.NoInlining)] private static object Compile(string source) => new AloeCompiler().Compile(HostPrelude + source);
    [MethodImpl(MethodImplOptions.NoInlining)] private static (int Instructions, int Functions) Counts(object module) =>
        (((Module)module).Code.Count, ((Module)module).Functions.Count);
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (long Instructions, int Pointer) Run(object module, InstallationPaths paths, Stream input, Stream output, int maximumInstructions)
    {
        if (maximumInstructions is < 1 or > 10000000) { throw new InvalidOperationException("Invalid instruction budget."); }
        var program = (Module)module;
        // Scripts must not disable the host's instruction observer or enable parallel tick execution.
        if (program.Code.Any(instruction => instruction.Opcode == EnumOpcode.Syscall
            && instruction.Operand0 is (int)EnumSyscall.GCSetDebug or (int)EnumSyscall.Tick))
        { throw new AloeExecutionFailure(ErrorCode.ALOE_RUNTIME_ERROR, -1, 0); }
        var settings = new AloeVmSettings { AllowedHostCapabilities = Aloe.RuntimeLib.AloeHostCapability.ConsoleInput, Debug = false,
            DataDirectory = paths.DataDirectory, Logging = new AloeLoggingSettings { Enabled = false,
                ConsoleEnabled = false, FileEnabled = false, Directory = paths.LogsDirectory } };
        var vm = new AloeVm(program, settings) { MaxVmThreads = 1, TraceEnabled = true,
            TraceWriter = _ => { }, OutputWriter = _ => throw new InvalidOperationException("Host I/O unavailable."),
            InputReader = () => throw new InvalidOperationException("Host I/O unavailable."),
            SleepHandler = _ => throw new InvalidOperationException("Host I/O unavailable.") };
        var sequence = 0;
        long instructions = 0;
        var pointer = -1;
        vm.TraceWriter = _ =>
        {
            pointer = vm.CurrentFrame?.Ip ?? -1;
            if (instructions >= maximumInstructions) { throw new AloeExecutionFailure(ErrorCode.ALOE_LIMIT_EXCEEDED, pointer, instructions); }
            instructions++;
        };
        vm.InputReader = () =>
        {
            var frame = vm.CurrentFrame;
            if (frame?.Function.Name is not ("execute_operation" or "register_signal" or "wait_for_signal" or "unregister_signal"))
            { throw new InvalidOperationException("Host I/O unavailable."); }
            var name = frame.Function.Name;
            var expectedParameters = name is "execute_operation" or "register_signal" or "wait_for_signal" ? 2 : 1;
            if (frame.Function.ParameterCount != expectedParameters || frame.Locals.Length < expectedParameters
                || frame.Locals.Take(expectedParameters).Any(static value => !value.IsString))
            { throw new InvalidOperationException("Host I/O unavailable."); }
            var arguments = frame.Locals.Take(expectedParameters).Select(static value => value.AsString).ToArray();
            var args = name switch
            {
                "execute_operation" => arguments[1],
                "register_signal" => JsonSerializer.Serialize(new { conditionJson = arguments[0], retentionSeconds = arguments[1] }),
                "wait_for_signal" => JsonSerializer.Serialize(new { signalId = arguments[0], timeoutMs = arguments[1] }),
                _ => JsonSerializer.Serialize(new { signalId = arguments[0] })
            };
            name = name switch
            {
                "register_signal" => "$aloe.register_signal",
                "wait_for_signal" => "$aloe.wait_for_signal",
                "unregister_signal" => "$aloe.unregister_signal",
                _ => arguments[0]
            };
            if (name.Length is < 1 or > 128 || args.Length > 65536) { throw new InvalidOperationException("Invalid host call."); }
            sequence = checked(sequence + 1);
            AloeWire.Write(output, new { status = "operation", sequence, name, args });
            var result = AloeWire.Read(input);
            if (result.GetProperty("status").GetString() != "operation_result" || result.GetProperty("sequence").GetInt32() != sequence)
            { throw new InvalidOperationException("Invalid host result."); }
            return result.GetProperty("data").GetString();
        };
        try { vm.RunFromEntryPoint(); }
        catch (AloeExecutionFailure) { throw; }
        catch (Exception) { throw new AloeExecutionFailure(ErrorCode.ALOE_RUNTIME_ERROR, pointer, instructions); }
        return (instructions, pointer);
    }
    private sealed class AloeExecutionFailure(ErrorCode code, int pointer, long instructions) : Exception("Aloe execution failed.")
    {
        internal ErrorCode Code { get; } = code;
        internal int Pointer { get; } = pointer;
        internal long Instructions { get; } = instructions;
    }
    private static async ValueTask ErrorAsync(Stream output, ErrorCode code, string stage = "compile")
    {
        try { await AloeWire.WriteAsync(output, new { status = "error", code = code.ToString(), stage }, CancellationToken.None); }
        catch (IOException) { Console.Error.WriteLine("Aloe helper channel closed."); }
    }
}
