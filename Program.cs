using CsSsg.Program;

var cmd = args[0];
var cmdArgs = args.Skip(1).ToArray();

switch (cmd) {
    case "loader":
        DirLoaderProgram.Run(cmdArgs);
        break;
    case "server":
        WebServerProgram.Run(cmdArgs);
        break;
    default:
        // `dotnet ef dbcontext optimize` runs with the expectation of invoking WebServerProgram
        if (!cmd.StartsWith('-'))
            throw new InvalidOperationException($"Unknown command {cmd}");
        WebServerProgram.Run(args);
        break;
}
