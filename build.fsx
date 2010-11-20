#I @"lib"
#r "FakeLib.dll"
#r "Fake.Gallio.dll"
#r "System.Xml.Linq"
#load "fake.fsx"

open System
open System.IO
open System.Xml.Linq
open Fake
open Fake.FileUtils

let version = "0.3.0"
let buildDir = "merged"
let docsDir = "docs"
let docsFile = "SolrNet.chm"
let keyFile = pwd() @@ "mausch.snk"
let config = getBuildParamOrDefault "config" "debug"
let target = getBuildParamOrDefault "target" "BuildAll"

let slnBuild sln x = 
    let strongName = 
        if File.Exists keyFile
            then ["SignAssembly","true"; "AssemblyOriginatorKeyFile",keyFile]
            else []
    sln |> build (fun p -> { p with 
                                Targets = [x]
                                Properties = ["Configuration",config] @ strongName })

let mainSln = slnBuild "solrnet.sln"
let sampleSln = slnBuild "SampleSolrApp.sln"

Target "Clean" (fun _ -> 
    mainSln "Clean"
    sampleSln "Clean"
    rm_rf buildDir
)

Target "Build" (fun _ -> mainSln "Rebuild")
Target "BuildSample" (fun _ -> sampleSln "Rebuild")

let libs = ["SolrNet"; "SolrNet.DSL"; "HttpWebAdapters"; "Castle.Facilities.SolrNetIntegration"; "Ninject.Integration.SolrNet"; "NHibernate.SolrNet"; "StructureMap.SolrNetIntegration"]
let dlls = [for l in libs -> l + ".dll"]
let dirs = [for l in libs -> l @@ "bin" @@ config]

let testAssemblies = !! ("**/bin/"+config+"/*Tests.dll")
let noIntegrationTests = "exclude Category: Integration"
let onlyIntegrationTests = "Category: Integration"

Target "Test" (fun _ ->
    testAssemblies |> Gallio.Run (fun p -> { p with Filters = noIntegrationTests })
)

for lib in libs do
    Target ("Test." + lib) (fun _ ->
        !! ("**/bin/"+config+"/"+lib+".Tests.dll")
            |> Gallio.Run (fun p -> { p with Filters = noIntegrationTests })
    )

Target "Coverage" (fun _ ->
    testAssemblies |> Gallio.Run (fun p -> { p with 
                                                Filters = noIntegrationTests
                                                RunnerType = "NCover"
                                                PluginDirectories = ["lib"] })
)

Target "IntegrationTest" (fun _ ->
    Solr.start()
    try
        testAssemblies |> Gallio.Run (fun p -> { p with Filters = onlyIntegrationTests })
    finally
        Solr.stop()
)

let merge libraries = 
    rm_rf buildDir
    mkdir buildDir
    let main = "SolrNet\\bin" @@ config @@ "SolrNet.dll"
    let output = buildDir @@ dlls.[0]
    let snk = if File.Exists keyFile then keyFile else null
    ILMerge (fun p -> { p with 
                            ToolPath = "lib\\ilmerge.exe"
                            Libraries = libraries
                            SearchDirectories = dirs
                            Internalize = InternalizeExcept "ilmerge.exclude"
                            KeyFile = snk
                            XmlDocs = true
                       }) output main

Target "Merge" (fun _ ->
    dlls |> Seq.skip 1 |> merge
)

Target "BasicMerge" (fun _ ->
    dlls |> Seq.skip 1 |> Seq.take 2 |> merge
)

Target "Version" (fun _ ->
    for l in libs do
        AssemblyInfo (fun p -> { p with
                                    OutputFileName = l @@ "Properties\\AssemblyInfo.cs"
                                    CLSCompliant = true
                                    AssemblyTitle = l
                                    AssemblyDescription = l
                                    AssemblyProduct = l
                                    AssemblyInformationalVersion = Git.sha1()
                                    AssemblyCopyright = "Copyright Mauricio Scheffer 2007-" + DateTime.Now.Year.ToString()
                                    Guid = "6688f9b4-5f2d-4fd6-aafc-3a81c84a69f1"
                                    AssemblyVersion = version
                                    AssemblyFileVersion = version })
)

Target "Docs" (fun _ ->
    rm_rf docsDir
    let r = Shell.Exec(@"tools\doxygen\doxygen.exe")
    if r <> 0 then failwith "Doxygen failed"
    rm docsFile
    Rename docsFile (docsDir @@ "html\\index.chm")
    rm_rf docsDir
)

Target "ReleasePackage" (fun _ -> 
    let outputPath = "build"
    rm_rf outputPath
    mkdir outputPath
    cp "readme.txt" outputPath
    if File.Exists docsFile then
        cp docsFile outputPath

    !+ (buildDir @@ "SolrNet.*")
        ++ "license.txt" ++ "lib\\Microsoft.Practices.*"
        |> Scan
        |> Copy outputPath

    let unmerged = outputPath @@ "unmerged"
    mkdir unmerged

    for l in libs do
        !! (l @@ "bin" @@ config @@ (l + ".*"))
            |> Copy unmerged

    run "BasicMerge"
    cp (buildDir @@ "SolrNet.dll") unmerged
    !! (unmerged @@ "SolrNet.DSL.*") |> DeleteFiles
    !! (unmerged @@ "HttpWebAdapters.*") |> DeleteFiles

    let zipFile = "SolrNet-"+version+".zip"
    rm zipFile
    !! (outputPath @@ "**\\*") |> Zip outputPath zipFile

    rm_rf outputPath
)

Target "PackageSampleApp" (fun _ ->
    let outputSolr = buildDir @@ solr
    cp_r solr outputSolr
    rm_rf (outputSolr @@ "solr\\data")
    let logs = outputSolr @@ "logs"
    rm_rf logs
    mkdir logs

    cp_r "tools\\Cassini" (buildDir @@ "tools\\Cassini")

    let sampleApp = "SampleSolrApp"
    let outputSampleApp = buildDir @@ sampleApp
    cp_r sampleApp outputSampleApp
    rm_rf (outputSampleApp @@ "obj")
    rm_rf (outputSampleApp @@ "log.txt")
    rm_rf (outputSampleApp @@ "SampleSolrApp.sln.cache")
    mkdir (outputSampleApp @@ "lib")

    !+ (outputSampleApp @@ "bin\\*") 
        -- "**\\SampleSolrApp.*" -- "**\\SolrNet.*"
        |> Scan
        |> Copy (outputSampleApp @@ "lib")
   
    ["pingsolr.js"; "license.txt"; "runsample.bat"] |> Copy buildDir

    let csproj = outputSampleApp @@ "SampleSolrApp.csproj"
    let xml = XDocument.Load csproj
    let refs = xml.Elements() .> "ItemGroup" .> "Reference" .> "HintPath"
    refs
    |> Seq.filter (startsWith @"..\lib")
    |> Seq.iter (replaceValue @"..\" "")
    refs
    |> Seq.filter (contains "SolrNet.dll")
    |> Seq.iter (setValue @"..\SolrNet.dll")
    xml.Save csproj
    
    !! (buildDir @@ "**\\*")
        |> Zip buildDir ("SolrNet-"+version+"-sample.zip")
)


Target "BuildAll" DoNothing
Target "TestAndRelease" DoNothing
Target "BuildAndRelease" DoNothing

"Test" <== ["BuildAll"]
"BuildAll" <== ["Build";"Merge";"BuildSample"]
"BuildAndRelease" <== ["Clean";"Version";"BuildAll";"Docs";"ReleasePackage"]
"TestAndRelease" <== ["Clean";"Version";"Test";"ReleasePackage"]

Run target