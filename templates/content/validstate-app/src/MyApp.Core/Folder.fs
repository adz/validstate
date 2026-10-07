namespace MyApp

open System
open System.IO
open Axial
open Axial.FileSystem

type FileEntry =
    { Name: string
      Size: int64
      IsFolder: bool }

/// Why a folder could not be listed. Renders itself, so it is safe to show and safe under NativeAOT.
type ListError =
    | FolderMissing of path: string
    | Unreadable of path: string * reason: string

    override this.ToString() =
        match this with
        | FolderMissing path -> $"Folder not found: {path}"
        | Unreadable(path, reason) -> $"Could not read {path}: {reason}"

module Folder =
    /// The folder's entries, folders first, then files, each alphabetical. All file access goes through
    /// Axial's IFileSystem service, so tests can run it against a temp folder and Guardrails can see it.
    let list (folder: string) : Flow<'env, ListError, FileEntry list> when 'env :> IHasFileSystem =
        flow {
            let! fileSystem = FileSystem.service

            if not (fileSystem.DirectoryExists folder) then
                return! Flow.fail (FolderMissing folder)
            else
                let! entries =
                    FileSystem.getFileSystemEntries folder "*" SearchOption.TopDirectoryOnly
                    |> Flow.mapError (fun error -> Unreadable(folder, FileSystemError.describe error))

                return
                    entries
                    |> Seq.map (fun path ->
                        let isFolder = fileSystem.DirectoryExists path

                        { Name = Path.GetFileName path
                          Size = if isFolder then 0L else fileSystem.GetFileLength path
                          IsFolder = isFolder })
                    |> Seq.sortBy (fun entry -> not entry.IsFolder, entry.Name.ToLowerInvariant())
                    |> List.ofSeq
        }

    let matches (filter: string) (entry: FileEntry) =
        String.IsNullOrWhiteSpace filter
        || entry.Name.Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase)
