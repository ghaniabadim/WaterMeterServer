namespace WaterMeterServer.FotaSimulator
{
    public enum SimulationScenario
    {
        Success,
        CrcRetry,
        Resume,
        DownloadFailure,
        InstallFailure
    }

    public sealed class SimulatorOptions
    {
        public string Host { get; private set; } = "127.0.0.1";
        public int Port { get; private set; } = 502;
        public string MeterId { get; private set; } = string.Empty;
        public string AesKey { get; private set; } = string.Empty;
        public int ChunkSize { get; private set; } = 256;
        public int TimeoutSeconds { get; private set; } = 120;
        public SimulationScenario Scenario { get; private set; } = SimulationScenario.Success;

        public static SimulatorOptions Create(
            string host,
            int port,
            string meterId,
            string aesKey,
            int chunkSize,
            int timeoutSeconds,
            SimulationScenario scenario)
        {
            var options = new SimulatorOptions
            {
                Host = host,
                Port = port,
                MeterId = meterId,
                AesKey = aesKey,
                ChunkSize = chunkSize,
                TimeoutSeconds = timeoutSeconds,
                Scenario = scenario
            };

            Validate(options);
            return options;
        }

        public static SimulatorOptions Parse(string[] args)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < args.Length; index++)
            {
                string argument = args[index];
                if (!argument.StartsWith("--", StringComparison.Ordinal))
                {
                    throw new ArgumentException($"Unexpected argument: {argument}");
                }

                if (index + 1 >= args.Length)
                {
                    throw new ArgumentException($"Missing value for {argument}");
                }

                values[argument[2..]] = args[++index];
            }

            var options = new SimulatorOptions
            {
                Host = Get(values, "host") ?? "127.0.0.1",
                Port = ParseInt(Get(values, "port"), 502, "port"),
                MeterId = Get(values, "meter-id") ?? string.Empty,
                AesKey = Get(values, "key") ??
                    Environment.GetEnvironmentVariable("Protocol__AesKey") ??
                    string.Empty,
                ChunkSize = ParseInt(Get(values, "chunk-size"), 256, "chunk-size"),
                TimeoutSeconds = ParseInt(Get(values, "timeout"), 120, "timeout"),
                Scenario = ParseScenario(Get(values, "scenario"))
            };

            Validate(options);
            return options;
        }

        private static void Validate(SimulatorOptions options)
        {
            if (string.IsNullOrWhiteSpace(options.Host))
            {
                throw new ArgumentException("Host is required.", nameof(options.Host));
            }

            if (string.IsNullOrWhiteSpace(options.MeterId) ||
                options.MeterId.Length > 34 ||
                options.MeterId.Length % 2 != 0 ||
                options.MeterId.Any(character => character is < '0' or > '9'))
            {
                throw new ArgumentException(
                    "--meter-id is required and must contain an even number of 2 to 34 decimal digits.");
            }

            if (string.IsNullOrWhiteSpace(options.AesKey))
            {
                throw new ArgumentException(
                    "Set Protocol__AesKey or provide --key with the active AES key in hexadecimal format.");
            }

            if (options.Port is < 1 or > 65535)
            {
                throw new ArgumentOutOfRangeException(nameof(options.Port));
            }

            if (options.ChunkSize is < 1 or > 2048)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(options.ChunkSize),
                    "Chunk size must be between 1 and 2048 bytes.");
            }

            if (options.TimeoutSeconds is < 1 or > 300)
            {
                throw new ArgumentOutOfRangeException(nameof(options.TimeoutSeconds));
            }
        }

        private static string? Get(Dictionary<string, string> values, string key)
        {
            return values.TryGetValue(key, out string? value) ? value : null;
        }

        private static int ParseInt(string? value, int fallback, string name)
        {
            if (value == null)
            {
                return fallback;
            }

            return int.TryParse(value, out int result)
                ? result
                : throw new ArgumentException($"Invalid integer for --{name}: {value}");
        }

        private static SimulationScenario ParseScenario(string? value)
        {
            return value?.ToLowerInvariant() switch
            {
                null or "success" => SimulationScenario.Success,
                "crc-retry" => SimulationScenario.CrcRetry,
                "resume" => SimulationScenario.Resume,
                "download-failure" => SimulationScenario.DownloadFailure,
                "install-failure" => SimulationScenario.InstallFailure,
                _ => throw new ArgumentException(
                    "Scenario must be success, crc-retry, resume, download-failure, or install-failure.")
            };
        }
    }
}
