namespace WebFsc.Server

open System
open System.IO
open System.Text.Json
open Microsoft.AspNetCore
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.StaticFiles
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.FileProviders
open Bolero
open Bolero.Server

type Startup() =

    let contentTypeProvider = FileExtensionContentTypeProvider()
    do  contentTypeProvider.Mappings.[".fsx"] <- "text/x-fsharp"
        contentTypeProvider.Mappings.[".scss"] <- "text/x-scss"
        contentTypeProvider.Mappings.[".wasm"] <- "application/wasm"
        contentTypeProvider.Mappings.[".dll"] <- "application/octet-stream"
        contentTypeProvider.Mappings.[".pdb"] <- "application/octet-stream"
        contentTypeProvider.Mappings.[".dat"] <- "application/octet-stream"
        contentTypeProvider.Mappings.[".blat"] <- "application/octet-stream"
    let clientProjPath = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "WebFsc.Client"))

    let hasBlazorFrameworkFiles (wwwrootPath: string) =
        let frameworkPath = Path.Combine(wwwrootPath, "_framework")
        File.Exists(Path.Combine(frameworkPath, "blazor.boot.json"))
        || File.Exists(Path.Combine(frameworkPath, "blazor.webassembly.js"))

    let tryFindBuiltWwwRoot (basePath: string) =
        if Directory.Exists(basePath) then
            Directory.EnumerateDirectories(basePath)
            |> Seq.map (fun tfmPath -> Path.Combine(tfmPath, "wwwroot"))
            |> Seq.tryFind hasBlazorFrameworkFiles
        else
            None

    let tryFindBuiltOutputRoot (basePath: string) =
        if Directory.Exists(basePath) then
            Directory.EnumerateDirectories(basePath)
            |> Seq.tryPick (fun tfmPath ->
                let probe = Path.Combine(tfmPath, "FSharp.Core.dll")
                if File.Exists(probe) then Some tfmPath else None)
        else
            None

    let getClientWwwRoot () =
        let envPath = Environment.GetEnvironmentVariable("WEBFSC_CLIENT_WWWROOT")
        let hasEnvPath =
            not (String.IsNullOrWhiteSpace(envPath)) && Directory.Exists(envPath)
        if hasEnvPath then
            envPath
        else
            let builtWwwRoot =
                [ Path.Combine(clientProjPath, "bin", "Debug")
                  Path.Combine(clientProjPath, "bin", "Release") ]
                |> Seq.tryPick tryFindBuiltWwwRoot
            defaultArg builtWwwRoot (Path.Combine(clientProjPath, "wwwroot"))

    let getClientOutputRoot (clientWwwRoot: string) =
        let tryParent =
            match Directory.GetParent(clientWwwRoot) with
            | null -> None
            | parent ->
                let probe = Path.Combine(parent.FullName, "FSharp.Core.dll")
                if File.Exists(probe) then Some parent.FullName else None
        match tryParent with
        | Some _ -> tryParent
        | None ->
            [ Path.Combine(clientProjPath, "bin", "Debug")
              Path.Combine(clientProjPath, "bin", "Release") ]
            |> Seq.tryPick tryFindBuiltOutputRoot

    let tryFindStaticWebAssetsManifest () =
        let objPath = Path.Combine(clientProjPath, "obj")
        if Directory.Exists(objPath) then
            Directory.EnumerateFiles(objPath, "staticwebassets.build.json", SearchOption.AllDirectories)
            |> Seq.sortByDescending File.GetLastWriteTimeUtc
            |> Seq.tryHead
        else
            None

    let tryLoadPackageContentRoots () =
        match tryFindStaticWebAssetsManifest () with
        | None -> []
        | Some manifestPath ->
            try
                use stream = File.OpenRead(manifestPath)
                use doc = JsonDocument.Parse(stream)
                doc.RootElement.GetProperty("Assets").EnumerateArray()
                |> Seq.choose (fun asset ->
                    let basePath = asset.GetProperty("BasePath").GetString()
                    let contentRoot = asset.GetProperty("ContentRoot").GetString()
                    if String.IsNullOrWhiteSpace(basePath) || String.IsNullOrWhiteSpace(contentRoot) then
                        None
                    elif basePath.StartsWith("_content/", StringComparison.Ordinal) || basePath = "_content" then
                        Some (basePath, contentRoot)
                    else
                        None)
                |> Seq.distinct
                |> Seq.toList
            with _ ->
                []

    member this.ConfigureServices(services: IServiceCollection) =
        services.AddControllers() |> ignore

    member this.Configure(app: IApplicationBuilder, env: IWebHostEnvironment) =
        let clientWwwRoot = getClientWwwRoot()
        let clientFileProvider = new PhysicalFileProvider(clientWwwRoot)
        let clientOutputRoot = getClientOutputRoot clientWwwRoot
        let webRootFileProvider =
            new CompositeFileProvider(clientFileProvider, env.WebRootFileProvider)
        env.WebRootFileProvider <- webRootFileProvider
        let packageContentRoots = tryLoadPackageContentRoots ()

        if not (hasBlazorFrameworkFiles clientWwwRoot) then
            Console.WriteLine(
                "Client WebAssembly assets not found. Build WebFsc.Client to generate wwwroot/_framework or set WEBFSC_CLIENT_WWWROOT.")
        if List.isEmpty packageContentRoots then
            Console.WriteLine("No static web assets manifest found for client packages.")
        if Option.isNone clientOutputRoot then
            Console.WriteLine("Client reference assemblies not found. Build WebFsc.Client to enable /refs.")

        let frameworkPath = Path.Combine(clientWwwRoot, "_framework")
        let app: IApplicationBuilder =
            if Directory.Exists(frameworkPath) then
                app.UseStaticFiles(
                    StaticFileOptions(
                        ContentTypeProvider = contentTypeProvider,
                        FileProvider = new PhysicalFileProvider(frameworkPath),
                        RequestPath = PathString("/_framework")))
            else
                app

        let app =
            packageContentRoots
            |> List.fold (fun (app: IApplicationBuilder) (basePath, contentRoot) ->
                let requestPath = PathString("/" + basePath.TrimStart('/'))
                app.UseStaticFiles(
                    StaticFileOptions(
                        ContentTypeProvider = contentTypeProvider,
                        FileProvider = new PhysicalFileProvider(contentRoot),
                        RequestPath = requestPath))
            ) app

        let app =
            match clientOutputRoot with
            | Some outputRoot ->
                app.UseStaticFiles(
                    StaticFileOptions(
                        ContentTypeProvider = contentTypeProvider,
                        FileProvider = new PhysicalFileProvider(outputRoot),
                        RequestPath = PathString("/refs")))
            | None ->
                app

        app.UseStaticFiles(
                StaticFileOptions(
                    ContentTypeProvider = contentTypeProvider,
                    FileProvider = webRootFileProvider))
            .UseRouting()
            .UseEndpoints(fun endpoints ->
                endpoints.MapControllers() |> ignore
                endpoints.MapFallbackToFile("index.html") |> ignore)
        |> ignore

module Program =

    [<EntryPoint>]
    let main args =
        WebHost
            .CreateDefaultBuilder(args)
            .UseStartup<Startup>()
            .Build()
            .Run()
        0
