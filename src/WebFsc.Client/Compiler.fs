// $begin{copyright}
//
// Copyright (c) 2018 IntelliFactory and contributors
//
// Licensed under the Apache License, Version 2.0 (the "License"); you
// may not use this file except in compliance with the License.  You may
// obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or
// implied.  See the License for the specific language governing
// permissions and limitations under the License.
//
// $end{copyright}

namespace WebFsc.Client
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Diagnostics
open FSharp.Compiler.EditorServices
open FSharp.Compiler.Text
open System
open System.IO
open System.Net.Http
open System.Reflection
open System.Runtime.InteropServices
//open Microsoft.FSharp.Compiler
//open Microsoft.FSharp.Compiler.SourceCodeServices
open Microsoft.JSInterop
open FSharp.Compiler.Symbols

type CompilerStatus =
    | Standby
    | Running
    | Failed of FSharpDiagnostic[]
    | Succeeded of string * FSharpDiagnostic[]

/// Cache the parse and check results for a given file.
type FileResults =
    {
        Parse: FSharpParseFileResults
        Check: FSharpCheckFileResults
    }

module FileResults =

    let OfRes (parseRes, checkRes) =
        {
            Parse = parseRes
            Check = checkRes
        }

/// The compiler's state.
type Compiler =
    {
        Checker: FSharpChecker
        Options: FSharpProjectOptions
        CheckResults: FSharpCheckProjectResults
        MainFile: FileResults
        Sequence: int
        Status: CompilerStatus
    }

module Compiler =

    /// Dummy project file path needed by the checker API. This file is never actually created.
    let projFile = "/tmp/out.fsproj"
    /// The input F# source file path.
    let inFile = "/tmp/Main.fs"
    /// The default output assembly file path.
    let outFile = "/tmp/out.exe"

    /// <summary>
    /// Create checker options.
    /// </summary>
    /// <param name="checker">The F# code checker.</param>
    /// <param name="outFile"></param>
    let Options (checker: FSharpChecker) (outFile: string) =
        checker.GetProjectOptionsFromCommandLineArgs(projFile, [|
            "--simpleresolution"
            "--optimize-"
            "--noframework"
            "--fullpaths"
            "--warn:3"
            "--target:exe"
            "--targetprofile:netcore"
            inFile
            // Necessary standard library
            "-r:/tmp/FSharp.Core.dll"
            "-r:/tmp/System.Private.CoreLib.dll"
            "-r:/tmp/netstandard.dll"
            "-r:/tmp/System.dll"
            "-r:/tmp/System.Core.dll"
            "-r:/tmp/System.Collections.dll"
            "-r:/tmp/System.Console.dll"
            "-r:/tmp/System.IO.dll"
            "-r:/tmp/System.Numerics.dll"
            "-r:/tmp/System.Runtime.dll"
            "-r:/tmp/System.Runtime.Extensions.dll"
            // Additional libraries we want to make available
            "-r:/tmp/System.Net.Http.dll"
            "-r:/tmp/System.Threading.dll"
            "-r:/tmp/System.Threading.Tasks.dll"
            "-r:/tmp/System.Xml.Linq.dll"
            "-r:/tmp/WebFsc.Env.dll"
            "-o:" + outFile
        |])

    let referenceFiles =
        [ "FSharp.Core.dll"
          "System.Private.CoreLib.dll"
          "netstandard.dll"
          "System.dll"
          "System.Core.dll"
          "System.Collections.dll"
          "System.Console.dll"
          "System.IO.dll"
          "System.Numerics.dll"
          "System.Runtime.dll"
          "System.Runtime.Extensions.dll"
          "System.Net.Http.dll"
          "System.Threading.dll"
          "System.Threading.Tasks.dll"
          "System.Xml.Linq.dll"
          "WebFsc.Env.dll" ]

    let tryDownload (http: HttpClient) (url: string) = async {
        try
            use! response = http.GetAsync(url) |> Async.AwaitTask
            if response.IsSuccessStatusCode then
                let! bytes = response.Content.ReadAsByteArrayAsync() |> Async.AwaitTask
                return Some bytes
            else
                return None
        with _ ->
            return None
    }

    let ensureReferenceFile (http: HttpClient) (fileName: string) = async {
        let targetPath = Path.Combine("/tmp", fileName)
        if not (File.Exists(targetPath)) then
            Directory.CreateDirectory("/tmp") |> ignore
            let! bytes =
                async {
                    let! fromRefs = tryDownload http ("refs/" + fileName)
                    match fromRefs with
                    | Some bytes -> return Some bytes
                    | None -> return! tryDownload http ("_framework/" + fileName)
                }
            match bytes with
            | Some bytes -> File.WriteAllBytes(targetPath, bytes)
            | None -> eprintfn "Missing reference %s from /refs or /_framework" fileName
    }

    let ensureReferences (http: HttpClient) = async {
        for fileName in referenceFiles do
            do! ensureReferenceFile http fileName
    }

    /// <summary>
    /// Create a compiler instance.
    /// </summary>
    /// <param name="source">The initial contents of Main.fs</param>
    let Create (http: HttpClient) (source:string) = async {
        let isBrowser =
            RuntimeInformation.IsOSPlatform(OSPlatform.Create("BROWSER"))
            || Environment.GetEnvironmentVariable("FCS_BROWSER") = "1"
        printfn "Compiler.Create: ensureReferences"
        do! ensureReferences http
        printfn "Compiler.Create: references ready"
        let checker = FSharpChecker.Create(keepAssemblyContents = true)
        let options = Options checker outFile
        File.WriteAllText(inFile, source)
        printfn "Compiler.Create: ParseAndCheckProject"
        let! checkRes = checker.ParseAndCheckProject(options)
        printfn "Compiler.Create: GetBackgroundCheckResultsForFileInProject"
        let! fileRes = checker.GetBackgroundCheckResultsForFileInProject(inFile, options)
        // The first compilation takes longer, so we run one during load
        if not isBrowser then
            let args = Array.append options.OtherOptions options.SourceFiles
            printfn "Compiler.Create: warmup compile"
            let! _ = checker.Compile(args)
            printfn "Compiler.Create: warmup compile done"
        else
            printfn "Compiler.Create: skip warmup compile in browser"
        return {
            Checker = checker
            Options = options
            CheckResults = checkRes
            MainFile = FileResults.OfRes fileRes
            Sequence = 0
            Status = Standby
        }
    }

    /// <summary>
    /// Check whether compilation has failed.
    /// </summary>
    /// <param name="errors">The messages returned by the compiler</param>
    let IsFailure (errors: seq<FSharpDiagnostic>) =
        errors
        |> Seq.exists (fun (x: FSharpDiagnostic) -> x.Severity = FSharpDiagnosticSeverity.Error)

    /// <summary>
    /// Turn a file in the virtual filesystem into a browser download.
    /// </summary>
    /// <param name="path">The file's location in the virtual filesystem</param>
    let DownloadFile (js: IJSInProcessRuntime) (path: string) =
        printfn "Downloading output..."
        try js.Invoke<unit>("WebFsc.getCompiledFile", path)
        with exn -> eprintfn "%A" exn

    /// <summary>
    /// Set the HttpClient used by user code via Env.Http.
    /// </summary>
    /// <param name="http"></param>
    let SetEnvHttpClient http =
        Env.SetHttp http

    let asyncMainTypeName = "Microsoft.FSharp.Core.unit -> \
                            Microsoft.FSharp.Control.Async<Microsoft.FSharp.Core.unit>"

    /// <summary>
    /// Check whether the code contains a function <c>Main.AsyncMain : unit -> Async&lt;unit&gt;</c>.
    /// </summary>
    /// <param name="checkRes">The compiler check results</param>
    let findAsyncMain (checkRes: FSharpCheckProjectResults) =
        match checkRes.AssemblySignature.FindEntityByPath ["Main"] with
        | Some m ->
            m.MembersFunctionsAndValues
            |> Seq.exists (fun v ->
                v.IsModuleValueOrMember &&
                v.LogicalName = "AsyncMain" &&
                v.FullType.Format(FSharpDisplayContext.Empty) = asyncMainTypeName
            )
        | None -> false

    /// <summary>
    /// Filter out "Main module of program is empty: nothing will happen when it is run"
    /// when the program has a function <c>Main.AsyncMain : unit -> Async&lt;unit&gt;</c>.
    /// </summary>
    /// <param name="checkRes">The compiler check results</param>
    /// <param name="errors">The parse and check messages</param>
    let filterNoMainMessage checkRes (errors: FSharpDiagnostic[]) =
        if findAsyncMain checkRes then
            errors |> Array.filter (fun m -> m.ErrorNumber <> 988)
        else
            errors

    /// The delayer for triggering type checking on user input.
    let checkDelay = Delayer(500)

open Compiler
open FSharp.Compiler.EditorServices

type Compiler with

    /// <summary>
    /// Compile an assembly.
    /// </summary>
    /// <param name="source">The source of Main.fs.</param>
    /// <returns>The compiler in "Running" mode and the callback to complete the compilation</returns>
    member comp.Run(source: string) =
        { comp with Status = Running },
        fun () -> async {
            let start = DateTime.Now
            let outFile = sprintf "/tmp/out%i.exe" comp.Sequence
            File.WriteAllText(inFile, source)
            // We need to recompute the options because we're changing the out file
            let options = Compiler.Options comp.Checker outFile
            let! checkRes = comp.Checker.ParseAndCheckProject(options)
            if IsFailure checkRes.Diagnostics then return { comp with Status = Failed checkRes.Diagnostics } else
            let args = Array.append options.OtherOptions options.SourceFiles
            let! (errors: FSharpDiagnostic[]), (outCode: exn option) = comp.Checker.Compile(args)
            let finish = DateTime.Now
            printfn "Compiled in %A" (finish - start)
            let errors =
                Array.append checkRes.Diagnostics errors
                |> filterNoMainMessage checkRes
            if IsFailure errors || Option.isSome outCode then return { comp with Status = Failed errors } else
            return
                { comp with
                    Sequence = comp.Sequence + 1
                    Status = Succeeded (outFile, errors) }
        }

    /// <summary>
    /// Trigger code checking.
    /// Includes auto-delay, so can (and should) be called on every user input.
    /// </summary>
    /// <param name="source">The source of Main.fs</param>
    /// <param name="dispatch">The callback to dispatch the results</param>
    member comp.TriggerCheck(source: string, dispatch: Compiler * FSharpDiagnostic[] -> unit) =
        checkDelay.Trigger(async {
            let! parseRes, checkRes = comp.Checker.ParseAndCheckFileInProject(inFile, 0, SourceText.ofString source, comp.Options)
            let checkRes =
                match checkRes with
                | FSharpCheckFileAnswer.Succeeded res -> res
                | FSharpCheckFileAnswer.Aborted -> comp.MainFile.Check
            dispatch
                ({ comp with MainFile = FileResults.OfRes (parseRes, checkRes) },
                Array.append parseRes.Diagnostics checkRes.Diagnostics)
        })

    /// <summary>
    /// Get autocompletion items.
    /// </summary>
    /// <param name="line">The line where code has been input</param>
    /// <param name="col">The column where code has been input</param>
    /// <param name="lineText">The text of the line that has changed</param>
    member comp.Autocomplete(line: int, col: int, lineText: string) = async {
        let partialName = QuickParse.GetPartialLongNameEx(lineText, col)
        let res = comp.MainFile.Check.GetDeclarationListInfo(Some comp.MainFile.Parse, line, lineText, partialName)
        return res.Items
    }

    /// The warnings and errors from the latest check.
    member comp.Messages =
        match comp.Status with
        | Standby | Running -> [||]
        | Succeeded(_, m) | Failed m -> m

    member comp.IsRunning =
        comp.Status = Running

    member comp.MarkAsFailedIfRunning() =
        match comp.Status with
        | Running -> { comp with Status = Failed [||] }
        | _ -> comp
