using System.Diagnostics;
using System.Text;

namespace EntregasApi.Services;

public interface IExternalProcessRunner
{
    Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> arguments, CancellationToken cancellationToken);
}

public record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

public class ExternalProcessRunner : IExternalProcessRunner
{
    private readonly ILogger<ExternalProcessRunner> _logger;

    public ExternalProcessRunner(ILogger<ExternalProcessRunner> logger)
    {
        _logger = logger;
    }

    public async Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null) stdout.AppendLine(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null) stderr.AppendLine(e.Data);
        };

        _logger.LogInformation("Ejecutando proceso: {FileName} {Arguments}", fileName, string.Join(" ", psi.ArgumentList));

        if (!process.Start())
            throw new InvalidOperationException($"No se pudo iniciar el proceso {fileName}.");

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(cancellationToken);

        var result = new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString());
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"El proceso {fileName} termino con codigo {result.ExitCode}: {TrimForError(result.StandardError)}");
        }

        return result;
    }

    private static string TrimForError(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "sin detalle";
        value = value.Trim();
        return value.Length <= 1200 ? value : value[..1200];
    }
}
