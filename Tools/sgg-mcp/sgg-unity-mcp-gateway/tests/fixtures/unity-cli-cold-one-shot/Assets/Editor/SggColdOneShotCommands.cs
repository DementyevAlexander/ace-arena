using Unity.Pipeline.Commands;

public static class SggColdOneShotCommands
{
    [CliCommand(
        "sgg_cold_main_thread_noop",
        "Return a fixed value through the Pipeline main-thread gate",
        MainThreadRequired = true)]
    public static string MainThreadNoOp()
    {
        return "main-thread-ok";
    }

    [CliCommand(
        "sgg_cold_background_noop",
        "Return a fixed value without the Pipeline main-thread gate",
        MainThreadRequired = false)]
    public static string BackgroundNoOp()
    {
        return "background-ok";
    }
}
