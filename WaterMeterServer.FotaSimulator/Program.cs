namespace WaterMeterServer.FotaSimulator
{
    public static class Program
    {
        public static async Task<int> Main(string[] args)
        {
            try
            {
                SimulatorOptions options = SimulatorOptions.Parse(args);
                var simulator = new FotaTerminalSimulator(options);
                await simulator.RunAsync();
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"FOTA simulation failed: {ex.Message}");
                return 1;
            }
        }
    }
}
