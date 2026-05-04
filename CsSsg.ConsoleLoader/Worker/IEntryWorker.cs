using LanguageExt;

namespace CsSsg.ConsoleLoader.Worker;

internal interface IEntryWorker
{
    Task<Either<FileWorker.ErrorResult, BoxedObject>> PrepareEntryFromFileAsync(string file, CancellationToken token);
    Task<FileWorker.FileResult> TryUpdateAsync(object entry, Client client, CancellationToken token);
    Task<FileWorker.FileResult> TryCreateAsync(object entry, Client client, CancellationToken token);
    string PermissionsLink(string slug);

    readonly record struct BoxedObject(object Obj);
}
