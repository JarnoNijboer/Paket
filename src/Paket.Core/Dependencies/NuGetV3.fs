﻿/// Contains NuGet support.
module Paket.NuGetV3

open Newtonsoft.Json
open System.IO
open System.Collections.Generic

open Newtonsoft.Json.Serialization
open Paket
open System
open System.IO
open System.IO.Compression
open System.Text
open System.Threading
open System.Threading.Tasks
open System.Net
open System.Text.RegularExpressions
open Paket.Domain
open Paket.NuGetCache
open Paket.Utils
open Paket.Xml
open Paket.PackageSources
open Paket.Requirements
open Paket.Logging
open Paket.PlatformMatching
open FSharp.Polyfill

type NugetV3SourceResourceJSON =
    { [<JsonProperty("@type")>]
      Type : string
      [<JsonProperty("@id")>]
      ID : string }

type NugetV3SourceRootJSON =
    { [<JsonProperty("resources")>]
      Resources : NugetV3SourceResourceJSON [] }

//type NugetV3Source =
//    { Url : string
//      Authentication : NugetSourceAuthentication option }

type NugetV3ResourceType =
    | AutoComplete
    | AllVersionsAPI
    //| Registration
    | PackageIndex
    | Catalog

    member this.AsString =
        match this with
        | AutoComplete -> "SearchAutoCompleteService"
        //| Registration -> "RegistrationsBaseUrl"
        | AllVersionsAPI -> "PackageBaseAddress/3.0.0"
        | PackageIndex -> "PackageDisplayMetadataUriTemplate"
        | Catalog -> "Catalog"

// Cache for nuget indices of sources
type ResourceIndex = Map<NugetV3ResourceType,string>
let private nugetV3Resources = System.Collections.Concurrent.ConcurrentDictionary<NugetV3Source,Task<ResourceIndex>>()

let getNuGetV3Resource (source : NugetV3Source) (resourceType : NugetV3ResourceType) : Async<string> =
    let key = source
    let getResourcesRaw () =
        async {
            let basicAuth = source.Authentication
            let! rawData = safeGetFromUrl(basicAuth, source.Url, acceptJson)
            let rawData =
                match rawData with
                | Unauthorized ->
                    raise (new Exception(sprintf "Could not load resources from '%s': Unauthorized (401)" source.Url))
                | NotFound ->
                    raise (new Exception(sprintf "Could not load resources (404) from '%s'" source.Url))
                | UnknownError e ->
                    raise (new Exception(sprintf "Could not load resources from '%s'" source.Url, e.SourceException))
                | SuccessResponse x -> x

            let json = JsonConvert.DeserializeObject<NugetV3SourceRootJSON>(rawData)
            let resources =
                json.Resources
                |> Seq.distinctBy(fun x -> x.Type.ToLower())
                |> Seq.map(fun x -> x.Type.ToLower(), x.ID)
            let map =
                resources
                |> Seq.choose (fun (res, value) ->
                    let resType =
                        match res.ToLower() with
                        | "searchautocompleteservice" -> Some AutoComplete
                        //| "registrationsbaseurl" -> Some Registration
                        | s when s.StartsWith "packagedisplaymetadatauritemplate" -> Some PackageIndex
                        | "packagebaseaddress/3.0.0" -> Some AllVersionsAPI
                        | s when s.StartsWith "catalog/3.0" -> Some Catalog
                        | _ -> None
                    match resType with
                    | None -> None
                    | Some k ->
                        Some (k, value))
                |> Seq.distinctBy fst
                |> Map.ofSeq
            return map
        } |> Async.StartAsTask

    async {
        let t = nugetV3Resources.GetOrAdd(key, (fun _ -> getResourcesRaw()))
        let! res = t |> Async.AwaitTask
        return
            match res.TryFind resourceType with
            | Some s -> s
            | None -> failwithf "could not find an %s endpoint for %s" (resourceType.ToString()) source.Url
    }

/// [omit]
type JSONResource =
    { Type : string;
      ID: string }

/// [omit]
type JSONVersionData =
    { Data : string []
      Versions : string [] }

/// [omit]
type JSONRootData =
    { Resources : JSONResource [] }

/// [omit]
let private searchDict = new System.Collections.Concurrent.ConcurrentDictionary<_,System.Threading.Tasks.Task<_>>()

/// [omit]
let private allVersionsDict = new System.Collections.Concurrent.ConcurrentDictionary<_,System.Threading.Tasks.Task<_>>()

/// Calculates the NuGet v3 URL from a NuGet v2 URL.
let calculateNuGet3Path(nugetUrl:string) =
    match nugetUrl.TrimEnd([|'/'|]) with
    | "http://nuget.org/api/v2" -> Some "http://api.nuget.org/v3/index.json"
    | "https://nuget.org/api/v2" -> Some "https://api.nuget.org/v3/index.json"
    | "http://www.nuget.org/api/v2" -> Some "http://api.nuget.org/v3/index.json"
    | "https://www.nuget.org/api/v2" -> Some "https://api.nuget.org/v3/index.json"
    | url when url.EndsWith("/nuget/v2") && url.Contains("pkgs.visualstudio.com") -> Some (url.Replace("/nuget/v2","/nuget/v3/index.json"))
    | url when url.EndsWith("/nuget/v2") && url.Contains("/_packaging/") -> Some (url.Replace("/nuget/v2","/nuget/v3/index.json"))  // TFS
    | url when url.EndsWith("api/v2") && url.Contains("visualstudio.com") -> Some (url.Replace("api/v2","api/v3/index.json"))
    | url when url.EndsWith("api/v2") && url.Contains("myget.org") -> Some (url.Replace("api/v2","api/v3/index.json"))
    | url when url.EndsWith("v3/index.json") -> Some url
    | _ -> None

/// Calculates the NuGet v3 URL from a NuGet v2 URL.
let calculateNuGet2Path(nugetUrl:string) =
    match nugetUrl.TrimEnd([|'/'|]) with
    | "http://api.nuget.org/v3/index.json" -> Some "http://nuget.org/api/v2"
    | "https://api.nuget.org/v3/index.json" -> Some "https://nuget.org/api/v2"
    | "http://api.nuget.org/v3/index.json" -> Some "http://www.nuget.org/api/v2"
    | "https://api.nuget.org/v3/index.json" -> Some "https://www.nuget.org/api/v2"
    | url when url.EndsWith("/nuget/v3/index.json") -> Some (url.Replace("/nuget/v3/index.json","/nuget/v2"))
    | url when url.EndsWith("/api/v3/index.json") && url.Contains("visualstudio.com") -> Some (url.Replace("/api/v3/index.json",""))
    | url when url.EndsWith("/api/v3/index.json") && url.Contains("myget.org") -> Some (url.Replace("/api/v3/index.json",""))
    | url when url.EndsWith("v2") -> Some url
    | _ -> None


/// [omit]
let getSearchAPI(auth,nugetUrl) =
    searchDict.GetOrAdd(nugetUrl, fun nugetUrl ->
        async {
            match calculateNuGet3Path nugetUrl with
            | None -> return None
            | Some v3Path ->
                let source = { Url = v3Path; Authentication = auth }
                let! v3res = getNuGetV3Resource source AutoComplete |> Async.Catch
                return
                    match v3res with
                    | Choice1Of2 s -> Some s
                    | Choice2Of2 ex ->
                        if verbose then traceWarnfn "getSearchAPI: %s" (ex.ToString())
                        None
        } |> Async.StartAsTask)

/// [omit]
let getAllVersionsAPI(auth,nugetUrl) =
    allVersionsDict.GetOrAdd(nugetUrl, fun nugetUrl ->
        async {
            match calculateNuGet3Path nugetUrl with
            | None -> return None
            | Some v3Path ->
                let source = { Url = v3Path; Authentication = auth }
                let! v3res = getNuGetV3Resource source AllVersionsAPI |> Async.Catch
                return
                    match v3res with
                    | Choice1Of2 s -> Some s
                    | Choice2Of2 ex ->
                        if verbose then traceWarnfn "getAllVersionsAPI: %s" (ex.ToString())
                        None
        } |> Async.StartAsTask)
        
/// [omit]
let getCatalogAPI auth nugetUrl =
    let catalogApi =
        async {
            match calculateNuGet3Path nugetUrl with
            | None -> return None
            | Some v3Path ->
                let source = { Url = v3Path; Authentication = auth }
                let! v3res = getNuGetV3Resource source Catalog |> Async.Catch
                return
                    match v3res with
                    | Choice1Of2 s -> Some s
                    | Choice2Of2 ex ->
                        if verbose then traceWarnfn "getCatalogAPI: %s" (ex.ToString())
                        None       
        } |> Async.RunSynchronously 
    match catalogApi with 
    | Some url when url |> String.IsNullOrEmpty |> not -> url
    | _-> failwithf "NO NuGetV3 Catalog on %s" nugetUrl

type NugetV3CatalogIndexItem =
    {   [<JsonProperty("@id")>]
        Id : string
        [<JsonProperty("@type")>]
        ItemType : string   
        [<JsonProperty("commitId")>]
        CommitId : string
        [<JsonProperty("commitTimeStamp")>]
        CommitTimeStamp : DateTimeOffset
        [<JsonProperty("count")>]
        Count : int 
    }
    
/// large unused fields are commented-out
type NugetV3CatalogPageItem =
    {   //[<JsonProperty("@id")>]
        //Id : string
        [<JsonProperty("@type")>]
        ItemType : string   
        //[<JsonProperty("commitId")>]
        //CommitId : string
        [<JsonProperty("commitTimeStamp")>]
        CommitTimeStamp : DateTimeOffset
        [<JsonProperty("nuget:id")>]
        NuGetId : string
        [<JsonProperty("nuget:version")>]
        NuGetVersion : string
    }
    
type NugetV3CatalogPage =
    {   [<JsonProperty("commitId")>]
        CommitId : string
        [<JsonProperty("commitTimeStamp")>]
        CommitTimeStamp : DateTimeOffset
        [<JsonProperty("count")>]
        Count : int
        [<JsonProperty("items")>]
        Items : NugetV3CatalogPageItem []
    }
        
type NugetV3CatalogIndex =
    {   [<JsonProperty("commitId")>]
        CommitId : string
        [<JsonProperty("commitTimeStamp")>]
        CommitTimeStamp : DateTimeOffset
        [<JsonProperty("count")>]
        Count : int
        [<JsonProperty("items")>]
        Items : NugetV3CatalogIndexItem []
    }
    
/// [omit]
let private getCatalogIndex auth nugetUrl cancel =
    let catalogApi = getCatalogAPI auth nugetUrl
    let indexResponse =
        async {
            return! safeGetFromUrl (auth, catalogApi, acceptJson)
        } |> (fun future -> 
            Async.StartAsTaskProperCancel(future,TaskCreationOptions.None,cancel))
    match indexResponse.Result with
    | NotFound -> failwith "Catalog/3.0 Index NotFound/404"; ""
    | Unauthorized -> failwith "Catalog/3.0 Index Unauthorized/401"; ""
    | UnknownError e -> failwithf "Catalog/3.0 Index N/A %A" e; ""
    | SuccessResponse s -> s 
    |> JsonConvert.DeserializeObject<NugetV3CatalogIndex>

let private getHostSpecificFileName fromUrl =
    let normalizeFileName name =
        let invalid = String(Path.GetInvalidFileNameChars())
        let inmatch = sprintf "[%s]" (Regex.Escape(invalid))
        Regex.Replace(name, inmatch, "_")
    match Uri.TryCreate (fromUrl, UriKind.Absolute) with
    | true, uri -> WebUtility.UrlDecode uri.Host
    | _ -> normalizeFileName (WebUtility.UrlDecode fromUrl)

let private getCatalogPageDirectory(basePath:String,item:String) =
    let hostName = getHostSpecificFileName item
    let cachePath = Path.Combine(basePath, "catalog", hostName)
    let directory = new DirectoryInfo(cachePath)
    if directory.Exists |> not then
        traceWarnfn "Create page cache \"%s\" -- long delay expected" directory.FullName
        directory.Create()
    directory;
    
let private getPageFileContent(pageFileName:String) =
    let pageFileInfo = new FileInfo(pageFileName+".gz")
    if pageFileInfo.Exists then
        try 
            // File.ReadAllText(pageFileInfo.FullName)
            use archive = File.OpenRead(pageFileInfo.FullName)
            use compressed = new GZipStream(archive,CompressionMode.Decompress)
            use reader = new StreamReader(compressed,Encoding.UTF8)
            reader.ReadToEnd()
            |> JsonConvert.DeserializeObject<NugetV3CatalogPage> 
            |> Some
        with 
        | ex ->
            traceWarnfn "Cannot read/parse %A: %A" pageFileInfo ex
            pageFileInfo.Delete() 
            None
    else None
    
let private setPageFileContent(pageFileName:String,responseData:String) = 
    try
        // File.WriteAllText(pageFileName,responseData)
        let bytes = Encoding.UTF8.GetBytes(responseData)
        use archive = File.OpenWrite(pageFileName+".gz")
        use compressed = new GZipStream(archive,CompressionLevel.Optimal)
        compressed.Write(bytes,0,bytes.Length)
    with
    | ex -> traceWarnfn "Cannot over/write %A: %A" pageFileName ex
    
/// [omit]
let private getCatalogPage auth (item:NugetV3CatalogIndexItem)(basePath:String) cancel =
    let pageDirectory = getCatalogPageDirectory(basePath,item.Id)
    let pageFileNameOnly = 
        item.Id |> WebUtility.UrlDecode
        |> String.split[|'/'|] |> Array.last 
        |> Path.GetFileNameWithoutExtension
    let pageFileName = Path.Combine(pageDirectory.FullName, pageFileNameOnly)    
    let pageFileData = getPageFileContent pageFileName        
    match pageFileData with
    | Some pageContent when 
        pageContent.CommitId = item.CommitId && 
        pageContent.CommitTimeStamp = item.CommitTimeStamp -> pageContent
    | _ -> 
        use localCancel = new CancellationTokenSource(PackageResolver.RequestTimeout)
        use linkedCancel = 
            CancellationTokenSource.CreateLinkedTokenSource(localCancel.Token,cancel)
        let pageResponse =
            async {
                return! safeGetFromUrl (auth,item.Id,acceptJson)
            } |> 
            (fun future -> 
                Async.StartAsTaskProperCancel(future,TaskCreationOptions.None,cancel))
        let responseData = 
            match pageResponse.Result with
            | NotFound -> failwith "Catalog/3.0 Page NotFound/404"; ""
            | Unauthorized -> failwith "Catalog/3.0 Page Unauthorized/401"; ""
            | UnknownError e -> failwithf "Catalog/3.0 Page N/A %A" e; ""
            | SuccessResponse s -> s
        let pageContents = 
            responseData |> JsonConvert.DeserializeObject<NugetV3CatalogPage>
        setPageFileContent(pageFileName,pageContents |> JsonConvert.SerializeObject)
        pageContents;
        

type NugetV3PackageCatalog = {
    Source : String
    Cursor : DateTimeOffset
    Packages : Map<String,String list>
}

let private semVerOrder versions =
    let getSemVer original =
        let parts = 
            original |> String.split[|'!'|]
        try
            SemVer.Parse(parts.[0])        
        with
        | ex -> 
            { SemVer.Zero with 
                Original = Some (parts.[0]) }
                
    let semVerStr (semVersion:SemVerInfo) =
        let normal = semVersion.Normalize()
        let normalized = 
            match semVersion.BuildMetaData with
            | null | "" -> normal
            | some -> normal + "+" + some
        match semVersion.Original with
        | Some str when str = normalized -> str
        | Some str -> str + "!" + normalized
        | None -> normalized + "!!"
        
    versions 
    |> List.distinct
    |> List.map getSemVer
    |> List.sort
    |> List.map semVerStr
    
let private semVerOrder2 _ versions = semVerOrder versions
      
//let private 

let getCatalogCursor basePath serviceUrl =
    let hostName = getHostSpecificFileName serviceUrl
    let fullName = Path.Combine(basePath, hostName + ".txt")
    
    let mutable packName = ""
    let mutable versions = List.empty
    
    let mutable builder = {
        Source = serviceUrl
        Cursor = DateTimeOffset.MinValue
        Packages = Seq.empty |> Map.ofSeq
    }
    
    if File.Exists fullName then    
        let readLine = File.ReadLines fullName        
        for line in readLine do // deserialize
            match line with
            | null | "" -> ignore() // skip empty
            
            | a when a.[0] = ':' ->
                let cursor = a.Substring(1).Trim()                
                match DateTimeOffset.TryParse cursor with
                | true, cursor -> builder <- {
                    builder with Cursor = cursor}
                | false, _ -> ignore()
                
            | a when a.[0] = '*' ->
                match packName with
                | null | "" -> ignore()
                | name -> 
                    let ver = 
                        match Map.tryFind name builder.Packages with
                        | Some values -> versions @ values
                        | None -> versions
                        
                    builder <- { 
                        builder with 
                            Packages = builder.Packages.Add(name,ver)}
                    
                versions <- List.empty // cleanup
                packName <- a.Substring(1).Trim()
                
            | a when a.[0] = '`' -> ignore()
            | a when a.[0] = '.' -> ignore()
            | a when a.[0] = ',' -> ignore()
            | a when a.[0] = ';' -> ignore()
            | a when a.[0] = '>' -> ignore()
            | a when a.[0] = '<' -> ignore()
            | a when a.[0] = '/' -> ignore()    
            | a when a.[0] = '\\' -> ignore()
            | a when Char.IsControl(a.[0]) -> ignore()
            | a when Char.IsSeparator(a.[0]) -> ignore()
            | a when Char.IsWhiteSpace(a.[0]) -> ignore()
            
            | version -> versions <- version :: versions
    else 
        ignore()
    let catalog = {
        builder with
            Packages = builder.Packages
            |> Map.map (fun k v -> List.rev v) // we were pre-pending
            // |> Map.map semVerOrder2 -- we need original order here
    }
    catalog;
    
let catalogSemVer2ordered (catalog:NugetV3PackageCatalog) =
    { catalog with Packages = catalog.Packages |> Map.map semVerOrder2 }
    
let setCatalogCursor basePath catalog =
    let hostName = getHostSpecificFileName catalog.Source
    let fullName = Path.Combine(basePath, hostName + ".txt")
    
    let fileInfo = new FileInfo(fullName)
    let backFile = fileInfo.FullName + ".bak"
    if fileInfo.Exists then
        try
            fileInfo.CopyTo(backFile, true) |> ignore
        with 
        | ex -> verbosefn "%A" ex
    elif fileInfo.Directory.Exists then
        ignore()
    else
        fileInfo.Directory.Create()
        
    let nextFile = new FileInfo(fileInfo.FullName + ".tmp")        
    use textFile = nextFile.CreateText()
    
    textFile.WriteLine(":" + catalog.Cursor.ToString("O"))
    
    for package in catalog.Packages do    
        textFile.WriteLine("*" + package.Key)
        
        for version in package.Value do        
            textFile.WriteLine(version)
                        
    textFile.Close()    
    
    nextFile.Replace(fileInfo.FullName,backFile,true).Exists

let getCatalogUpdated auth basePath catalog cancel =
    let asyncRes = 
        async {
            let mutable builder = {
                Source = catalog.Source
                Cursor = catalog.Cursor
                Packages = catalog.Packages |> Map.map (fun k v -> List.rev v)
            }
            
            let catalogIndex = getCatalogIndex auth catalog.Source cancel
            assert (catalogIndex.CommitId |> String.IsNullOrWhiteSpace = false)
            
            for indexItem in catalogIndex.Items do
                if indexItem.CommitTimeStamp < catalog.Cursor then ignore()
                else
                
                let indexPage = getCatalogPage auth indexItem basePath cancel
                assert (indexPage.CommitId |> String.IsNullOrWhiteSpace = false)
                
                if indexPage.CommitTimeStamp < catalog.Cursor then ignore()
                else
                
                for pageItem in indexPage.Items do
                    if pageItem.CommitTimeStamp < catalog.Cursor then ignore()
                    else
                    
                    let version = 
                        match pageItem.ItemType with
                        | "nuget:PackageDelete" -> Some (Choice1Of2 pageItem.NuGetVersion)
                        | "nuget:PackageDetails" -> Some (Choice2Of2 pageItem.NuGetVersion)
                        | _ -> None

                    match version with 
                    | Some choice ->
                    
                        let versionList = 
                            match Map.tryFind pageItem.NuGetId builder.Packages with
                            | Some versions ->
                                match choice with 
                                | Choice1Of2 delete -> versions |> List.except [delete] 
                                | Choice2Of2 details -> details :: versions
                            | None -> 
                                match choice with 
                                | Choice1Of2 delete -> []
                                | Choice2Of2 details -> [details]
                        
                        match versionList with 
                        | [] ->
                            builder <- { 
                                builder with 
                                    Packages = builder.Packages.Remove(pageItem.NuGetId)}
                        | versions -> 
                            builder <- {
                                builder with 
                                    Packages = builder.Packages.Add(pageItem.NuGetId,versionList)}
                                                
                        builder <- {builder with Cursor = indexPage.CommitTimeStamp}
                        
                    | None -> ignore() // nothing to do here
            let updated = { 
                builder with
                    Packages = builder.Packages
                    |> Map.map (fun k v -> List.rev v) // we were pre-pending new items
                    |> Map.map semVerOrder2 }    
            return updated
        }
    Async.StartAsTaskProperCancel(asyncRes, TaskCreationOptions.None, cancel)
        
/// [omit]
let extractAutoCompleteVersions(response:string) =
    JsonConvert.DeserializeObject<JSONVersionData>(response).Data

/// [omit]
let extractVersions(response:string) =
    JsonConvert.DeserializeObject<JSONVersionData>(response).Versions


let internal findAutoCompleteVersionsForPackage(v3Url, auth, packageName:Domain.PackageName, includingPrereleases, maxResults) =
    async {
        let url = sprintf "%s?semVerLevel=2.0.0&id=%O&take=%d%s" v3Url packageName (max maxResults 100000) (if includingPrereleases then "&prerelease=true" else "")

        let! response = safeGetFromUrl(auth,url,acceptJson) // NuGet is showing old versions first
        return
            response
            |> SafeWebResult.map (fun text ->
                let versions =
                    let extracted = extractAutoCompleteVersions text
                    if extracted.Length > maxResults then
                        SemVer.SortVersions extracted |> Array.take maxResults
                    else
                        SemVer.SortVersions extracted
                versions)
    }

/// Uses the NuGet v3 autocomplete service to retrieve all package versions for the given package.
let FindAutoCompleteVersionsForPackage(nugetURL, auth, package, includingPrereleases, maxResults) =
    async {
        let! raw = findAutoCompleteVersionsForPackage(nugetURL, auth, package, includingPrereleases, maxResults)
        return raw
    }


let internal findVersionsForPackage(v3Url, auth, packageName:Domain.PackageName) =
    // Comment from http://api.nuget.org/v3/index.json
    // explicitely says
    // Base URL of Azure storage where NuGet package registration info for NET Core is stored, in the format https://api.nuget.org/v3-flatcontainer/{id-lower}/{id-lower}.{version-lower}.nupkg
    // so I guess we need to take "id-lower" here -> myget actually needs tolower
    let url = sprintf "%s%s/index.json?semVerLevel=2.0.0" v3Url (packageName.CompareString)
    NuGetRequestGetVersions.ofSimpleFunc url (fun _ ->
        async {
            let! response = safeGetFromUrl(auth,url,acceptJson) // NuGet is showing old versions first
            return
                response
                |> SafeWebResult.map (fun text ->
                    let versions = extractVersions text

                    SemVer.SortVersions versions)
        })

/// Uses the NuGet v3 service to retrieve all package versions for the given package.
let FindVersionsForPackage(nugetURL, auth, package) =
    findVersionsForPackage(nugetURL, auth, package)

/// [omit]
let extractPackages(response:string) =
    JsonConvert.DeserializeObject<JSONVersionData>(response).Data

let private getPackages(auth, nugetURL, packageNamePrefix, maxResults) = async {
    let! apiRes = getSearchAPI(auth,nugetURL) |> Async.AwaitTask
    match apiRes with
    | Some url ->
        let query = sprintf "%s?q=%s&take=%d" url packageNamePrefix maxResults
        let! response = safeGetFromUrl(auth,query,acceptJson)
        match SafeWebResult.asResult response with
        | Result.Ok text -> return  Result.Ok (extractPackages text)
        | Result.Error err -> return Result.Error err
    | None ->
        if verbose then tracefn "Could not calculate search api from %s" nugetURL
        return Result.Ok [||]
}

/// Uses the NuGet v3 autocomplete service to retrieve all packages with the given prefix.
let FindPackages(auth, nugetURL, packageNamePrefix, maxResults) =
    async {
        return! getPackages(auth, nugetURL, packageNamePrefix, maxResults)
    }


type CatalogDependency =
    { [<JsonProperty("id")>]
      Id : string
      [<JsonProperty("range")>]
      Range : string }
type CatalogDependencyGroup =
    { [<JsonProperty("targetFramework")>]
      TargetFramework : string
      [<JsonProperty("dependencies")>]
      Dependencies : CatalogDependency [] }
type Catalog =
    { [<JsonProperty("licenseUrl")>]
      LicenseUrl : string
      [<JsonProperty("listed")>]
      Listed : System.Nullable<bool>
      [<JsonProperty("version")>]
      Version : string
      [<JsonProperty("dependencyGroups")>]
      DependencyGroups : CatalogDependencyGroup [] }


type PackageIndexPackage =
    { [<JsonProperty("@type")>]
      Type: string
      [<JsonProperty("packageContent")>]
      DownloadLink: string
      [<JsonProperty("catalogEntry")>]
      PackageDetails: Catalog }

type PackageIndexPage =
    { [<JsonProperty("@id")>]
      Id: string
      [<JsonProperty("@type")>]
      Type: string
      [<JsonProperty("items")>]
      Packages: PackageIndexPackage []
      [<JsonProperty("count")>]
      Count: int
      [<JsonProperty("lower")>]
      Lower: string
      [<JsonProperty("upper")>]
      Upper: string }

type PackageIndex =
    { [<JsonProperty("@id")>]
      Id: string
      [<JsonProperty("items")>]
      Pages: PackageIndexPage []
      [<JsonProperty("count")>]
      Count : int }

let private getPackageIndexRaw (source : NugetV3Source) (packageName:PackageName) =
    async {
        let! registrationUrl = getNuGetV3Resource source PackageIndex
        let url = registrationUrl.Replace("{id-lower}", packageName.ToString().ToLower())
        let! rawData = safeGetFromUrl (source.Authentication, url, acceptJson)
        return
            match rawData with
            | Unauthorized -> raise (System.Exception(sprintf "could not get registration data (401) from '%s'" url))
            | NotFound -> None
            | UnknownError err ->
                raise (System.Exception(sprintf "could not get registration data from %s" url, err.SourceException))
            | SuccessResponse x -> Some (JsonConvert.DeserializeObject<PackageIndex>(x))
    }

let private getPackageIndexMemoized =
    memoizeAsync (fun (source, packageName) -> getPackageIndexRaw source packageName)
let getPackageIndex source packageName = getPackageIndexMemoized (source, packageName)


let private getPackageIndexPageRaw (source:NugetV3Source) (url:string) =
    async {
        let! rawData = safeGetFromUrl (source.Authentication, url, acceptJson)
        return
            match rawData with
            | Unauthorized -> raise (System.Exception(sprintf "could not get registration data (401) from '%s'" url))
            | NotFound -> raise (System.Exception(sprintf "could not get registration data (404) from '%s'" url))
            | UnknownError err ->
                raise (System.Exception(sprintf "could not get registration data from %s" url, err.SourceException))
            | SuccessResponse x -> JsonConvert.DeserializeObject<PackageIndexPage>(x)
    }

let private getPackageIndexPageMemoized =
    memoizeAsync (fun (source, url) -> getPackageIndexPageRaw source url)
let getPackageIndexPage source (page:PackageIndexPage) = getPackageIndexPageMemoized (source, page.Id)


let getRelevantPage (source:NugetV3Source) (index:PackageIndex) (version:SemVerInfo) =
    async {
        let normalizedVersion = SemVer.Parse (version.ToString().ToLowerInvariant())
        let pages =
            index.Pages
            |> Seq.filter (fun p -> SemVer.Parse (p.Lower.ToLowerInvariant()) <= normalizedVersion && normalizedVersion <= SemVer.Parse (p.Upper.ToLowerInvariant()))
            |> Seq.toList

        let tryFindOnPage (page:PackageIndexPage) = async {
            let! page = async {
                if page.Count > 0 && (isNull page.Packages || page.Packages.Length = 0) then
                    return! getPackageIndexPage source page
                else return page }
            if page.Count > 0 && (isNull page.Packages || page.Packages.Length = 0) then
                failwithf "Page '%s' should contain packages!" page.Id

            let packages =
                page.Packages
                    // TODO: This might need to be part of SemVer itself?
                    // This is our favorite package: nlog/5.0.0-beta03-tryoutMutex
                    |> Seq.filter (fun p -> SemVer.Parse (p.PackageDetails.Version.ToLowerInvariant()) = normalizedVersion)
                    |> Seq.toList
            match packages with
            | [ package ] -> return Some package
            | [] -> return None
            | h :: _ ->
                // Can happen in theory when multiple versions differ only in casing...
                traceWarnfn "Multiple package versions matched with '%O' on page '%s'" version page.Id
                return Some h }
        match pages with
        | [ page ] ->
            let! package = tryFindOnPage page
            match package with
            | Some package -> return Some package
            | _ -> return failwithf "Version '%O' should be part of part of page '%s' but wasn't." version page.Id
        | [] ->
            return None
        | multiple ->
            // This can happen theoretically because of ToLower, if someone is really crasy enough to upload a package
            // with differently cased build strings and if nuget makes a page split exactly at that point.
            let mutable result = None
            for page in multiple do
                if result.IsNone then
                    let! package = tryFindOnPage page
                    match package with
                    | Some package -> result <- Some package
                    | None -> ()
            match result with
            | Some result ->
                traceWarnfn "Mulitple pages of V3 index '%s' match with version '%O'" index.Id version
                return Some result
            | None ->
                return failwithf "Mulitple pages of V3 index '%s' match with version '%O'" index.Id version
    }

let getPackageDetails (source:NugetV3Source) (packageName:PackageName) (version:SemVerInfo) : Async<ODataSearchResult> =
    async {
        let! pageIndex = getPackageIndex source packageName// version
        match pageIndex with
        | None -> return EmptyResult
        | Some pageIndex ->
        let! relevantPage = getRelevantPage source pageIndex version
        match relevantPage with
        | None -> return EmptyResult
        | Some relevantPage ->
        let catalogData = relevantPage.PackageDetails
        let dependencyGroups, dependencies =
            if catalogData.DependencyGroups = null then
                [], []
            else
                let detect x =
                    match extractPlatforms false x with
                    | Some p -> p
                    | None ->
                        if not (x.StartsWith "_") then
                            Logging.traceErrorIfNotBefore ("Package", x, packageName, version) "Could not detect any platforms from '%s' in %O %O, please tell the package authors" x packageName version
                        ParsedPlatformPath.Empty
                catalogData.DependencyGroups |> Seq.map (fun group -> detect group.TargetFramework) |> Seq.toList,

                catalogData.DependencyGroups
                |> Seq.map(fun group ->
                    if group.Dependencies = null then
                        Seq.empty
                    else
                        group.Dependencies
                        |> Seq.map(fun dep -> dep, group.TargetFramework))
                |> Seq.concat
                |> Seq.map(fun (dep, targetFramework) ->
                    let targetFramework =
                        match targetFramework with
                        | null -> ParsedPlatformPath.Empty
                        | x -> detect x
                    (PackageName dep.Id), (VersionRequirement.Parse dep.Range), targetFramework)
                |> Seq.toList
        let unlisted =
            if catalogData.Listed.HasValue then
               not catalogData.Listed.Value
            else
                false

        let optimized, warnings =
            addFrameworkRestrictionsToDependencies dependencies dependencyGroups
        for warning in warnings do
            let message = warning.Format packageName version
            Logging.traceWarnIfNotBefore message "%s" message

        return
            { SerializedDependencies = []
              PackageName = packageName.ToString()
              SourceUrl = source.Url
              Unlisted = unlisted
              DownloadUrl = relevantPage.DownloadLink
              LicenseUrl = catalogData.LicenseUrl
              Version = version.Normalize()
              CacheVersion = NuGetPackageCache.CurrentCacheVersion }
            |> NuGetPackageCache.withDependencies optimized
            |> ODataSearchResult.Match
    }

let loadFromCacheOrGetDetails (force:bool)
                              (cacheFileName:string)
                              (source:NugetV3Source)
                              (packageName:PackageName)
                              (version:SemVerInfo) =
    async {
        if not force && File.Exists cacheFileName then
            try
                let json = File.ReadAllText(cacheFileName)
                let cachedObject = JsonConvert.DeserializeObject<NuGetPackageCache> json
                if cachedObject.CacheVersion <> NuGetPackageCache.CurrentCacheVersion then
                    let! details = getPackageDetails source packageName version
                    return true,details
                else
                    return false,ODataSearchResult.Match cachedObject
            with exn ->
                if verboseWarnings then
                    traceWarnfn "Possible Performance degradation, could not retrieve '%O' from cache: %O" packageName exn
                else
                    traceWarnIfNotBefore ("NuGetV3 n/a", packageName, exn.Message) "Possible Performance degradation, could not retrieve '%O' from cache: %s" packageName exn.Message
                let! details = getPackageDetails source packageName version
                return true,details
        else
            let! details = getPackageDetails source packageName version
            return true,details
    }

/// Uses the NuGet v3 registration endpoint to retrieve package details .
let GetPackageDetails (force:bool) (source:NugetV3Source) (packageName:PackageName) (version:SemVerInfo) : Async<ODataSearchResult> =
    getDetailsFromCacheOr
        force
        source.Url
        packageName
        version
        (fun () ->
            getPackageDetails source packageName version)
