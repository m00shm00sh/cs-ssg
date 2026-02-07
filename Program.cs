using CsSsg.Program;

var cmd = args[0];
var cmdArgs = args.Skip(1).ToArray();

switch (cmd) {
    case "loader":
        throw new NotImplementedException("in progress");
    case "server":
        WebServerProgram.Run(cmdArgs);
        break;
    default:
        throw new InvalidOperationException($"Unknown command {cmd}");
}
