namespace WebFsc.Server

open System
open System.IO
open Microsoft.AspNetCore
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.StaticFiles
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.FileProviders
open Bolero
open Bolero.Server
open Bolero.Templating.Server

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

    member this.ConfigureServices(services: IServiceCollection) =
        services.AddControllers() |> ignore
        services.AddHotReload(clientProjPath) |> ignore

    member this.Configure(app: IApplicationBuilder, env: IWebHostEnvironment) =
        let clientWwwRoot = getClientWwwRoot()
        let clientFileProvider = new PhysicalFileProvider(clientWwwRoot)
        let webRootFileProvider =
            new CompositeFileProvider(clientFileProvider, env.WebRootFileProvider)
        env.WebRootFileProvider <- webRootFileProvider

        if not (hasBlazorFrameworkFiles clientWwwRoot) then
            Console.WriteLine(
                "Client WebAssembly assets not found. Build WebFsc.Client to generate wwwroot/_framework or set WEBFSC_CLIENT_WWWROOT.")

        let frameworkPath = Path.Combine(clientWwwRoot, "_framework")
        let app =
            if Directory.Exists(frameworkPath) then
                app.UseStaticFiles(
                    StaticFileOptions(
                        ContentTypeProvider = contentTypeProvider,
                        FileProvider = new PhysicalFileProvider(frameworkPath),
                        RequestPath = PathString("/_framework")))
            else
                app

        app.UseStaticFiles(
                StaticFileOptions(
                    ContentTypeProvider = contentTypeProvider,
                    FileProvider = webRootFileProvider))
            .UseRouting()
            .UseEndpoints(fun endpoints ->
                endpoints.UseHotReload()
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
