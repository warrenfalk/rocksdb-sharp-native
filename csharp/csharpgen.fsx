// Learn more about F# at http://fsharp.org

open System
open System.Text.RegularExpressions

let rocksdbVersion = "5.18.3"

let download uri =
    use wc = new System.Net.WebClient()
    let uri = Uri(uri)
    wc.DownloadString(uri)

let cleanupLineEndings (s:string) = s.Replace("\r\n", "\n") 

type NativeArg = {
    index: int
    nativeType: string
    isDelegate: bool
    name: string
    ending: string
}

type NativeFunction = {
    name: string
    returnType: string
    args: NativeArg list
    comments: string
}

type NativeEnumValue = {
    name: string
    value: int option
}

type NativeEnum = {
    name: string
    values: NativeEnumValue list
    comment: string
}

type NativeTypeDef = {
    name: string
    comment: string
}

type RocksDbHeaderFileRegion = {
    title: string
    nativeFunctions: NativeFunction list
    nativeEnums: NativeEnum list
    nativeTypeDefs: NativeTypeDef list
}

// Each "region separator" ends the previous region and starts a new one, except the last and first of course
let regionSeparatorPattern = Regex(@"(?:\n\n\/\* ([A-Z][\S]+?) \*\/\n|\n\n\/\* ([A-Z].+?) \*\/\n\n)|#ifdef __cplusplus\n\}", RegexOptions.Compiled ||| RegexOptions.Multiline)
let regionTitle (m:Match) : string option =
    if m.Groups.Item(1).Success then Some (m.Groups.Item(1).Value)
    elif m.Groups.Item(2).Success then Some (m.Groups.Item(2).Value)
    else None

let commentPrologPattern = @"((?:\/\*+[^*]*\*+(?:[^/*][^*]*\*+)*\/|\s|//[^\n]*)*)"

let rocksDbNativeTypeDefPattern = Regex(commentPrologPattern + @"typedef\sstruct\srocksdb.*?_t\s+(rocksdb.*?_t);", RegexOptions.Compiled)
let rocksDbNativeTypeDefFromMatch (m:Match) = {
    name = m.Groups.Item(2).Value.Trim()
    comment = m.Groups.Item(1).Value.Trim()
    }

let parseNativeTypeDefs (source:string) =
    rocksDbNativeTypeDefPattern.Matches(source)
    |> Seq.cast<Match>
    |> Seq.map (rocksDbNativeTypeDefFromMatch)

let nativeEnumValuePattern = Regex(@"([0-9a-zA-Z_]+)\s*(?:=\s*([0-9]+))?,?", RegexOptions.Compiled)
let nativeEnumValueFromMatch (m:Match) = {
    name = m.Groups.Item(1).Value
    // TODO: figure out how to map non-existent values to an incrementing number (maybe fold state?)
    value = if m.Groups.Item(2).Success then Some (Int32.Parse(m.Groups.Item(2).Value)) else None
    }

let nativeEnumPattern = Regex(commentPrologPattern + @"enum ([a-zA-Z0-9_]+)?{(.*?)};", RegexOptions.Compiled ||| RegexOptions.Singleline);
let nativeEnumFromMatch (m:Match) =
    let body = m.Groups.Item(3).Value
    let values =
        nativeEnumValuePattern.Matches(body)
        |> Seq.cast<Match>
        |> Seq.map(nativeEnumValueFromMatch)
    {
        name = if (m.Groups.Item(2).Success) then (m.Groups.Item(2).Value) else ""
        values = Seq.toList values
        comment = (m.Groups.Item(1).Value.Trim())
    }

let parseNativeEnumerations body =
    nativeEnumPattern.Matches(body)
    |> Seq.cast<Match>
    |> Seq.map(nativeEnumFromMatch)

let nativeFunctionStartPattern = Regex(commentPrologPattern + @"extern\s+ROCKSDB_LIBRARY_API\s+([^\(]+)\(", RegexOptions.IgnoreCase ||| RegexOptions.Compiled ||| RegexOptions.Multiline ||| RegexOptions.Singleline)
let functionClosePattern = Regex(@"^\)\s*;[ \t]*", RegexOptions.Compiled)

let (|Regex|_|) (pattern:Regex) input =
    let m = pattern.Match(input)
    if m.Success then Some(input.Substring(m.Index + m.Length), (List.tail [ for g in m.Groups -> g.Value ]))
    else None

let parseTypeAndName (typeAndName:string) =
    let m = Regex.Match(typeAndName, @"\s+([a-zA-Z0-9][a-zA-Z0-9_]*)\s*$")
    let name = m.Groups.Item(1).Value
    let typename = typeAndName.Substring(0, m.Index).TrimStart()
    (typename, name)

let takeUntilCloseParen (s:string) =
    let (index, _) =
        s
        |> Seq.mapi (fun i c -> (i, c))
        |> Seq.scan (fun (a, depth) (index, char) -> (index, if char = '(' then depth + 1 elif char = ')' then depth - 1 else depth)) (0, 1)
        |> Seq.find (fun (_, depth) -> depth = 0)
    (s.Substring(0, index), s.Substring(index + 1))

let parseWhitespace input =
    let m = Regex.Match(input, @"\s*")
    (input.Substring(0, m.Length), input.Substring(m.Length))

let modifiersPattern = Regex(@"^(const|unsigned)\s+", RegexOptions.Compiled)
let parseArgModifier (args:string) =
    match args with
    | Regex modifiersPattern (args, [modifier]) -> (Some modifier, args)
    | _ -> (None, args)

let argTypePattern = Regex(@"^([a-zA-Z_0-9]+(?:\s+const\*|\s+const|\s*\*)*)\s*", RegexOptions.Compiled)
let parseArgType (args:string) =
    match args with
    | Regex argTypePattern (args, [argType]) -> (Some argType, args)
    | _ -> (None, args)

let delegateNamePattern = Regex(@"^\(\*([a-zA-Z_0-9]+)\)\s*\(\s*", RegexOptions.Compiled)
let parseDelegateSignature (args: string) =
    match args with
    | Regex delegateNamePattern (args, [delegateName]) ->
        
    | _ -> (None, None, None, args)
    

let parseNextNativeArg (args:string) =
    let (ws, args) = parseWhitespace args
    let (argMod, args) = parseArgModifier args
    let (argType, args) = parseArgType args
    let (dname, dargs, args) = parseDelegateSignature args
    (None, args)

let rec parseNativeArgs (args:string) = seq {
    let (nextArg, args) = parseNextNativeArg args
    match nextArg with
    | Some(nextArg) ->
        yield nextArg
        yield! parseNativeArgs args
    | None -> ()
    }

let parseNativeFunctions body :seq<NativeFunction> =
    let rec parseNextFunc remain :seq<NativeFunction> = seq {
        match remain with
        | Regex nativeFunctionStartPattern (remain, [prolog; typeAndName]) ->
            let (typename, name) = parseTypeAndName typeAndName
            let (argsUnparsed, remain) = takeUntilCloseParen remain
            let (argsWs, argsUnparsed) = parseWhitespace argsUnparsed
            let args = parseNativeArgs argsUnparsed
            yield {
                name = name
                returnType = typename
                args = List.empty<NativeArg>
                comments = (argsUnparsed.Trim())//(prolog.Trim())
            }
            yield! parseNextFunc remain
        | _ -> ()
    }
    parseNextFunc body

let parseRocksHeaderFileRegions (source:string) = 
    // This might be uglier than it needs to be, but it works fine
    // The pattern matches only the top line of each region
    // And so to fully define a region, we need to parse out the next one also
    regionSeparatorPattern.Matches(source)
    |> Seq.cast<Match>
    // need these in pairs because we want the information between them
    |> Seq.pairwise
    // get all the information from current using next to figure out where it ends
    |> Seq.map (fun (current, next) ->
        let blockStart = current.Index + current.Length
        let blockEnd = next.Index
        let blockLength = blockEnd - blockStart
        let title = regionTitle current
        match title with
        | Some title -> Some (blockStart, blockLength, title)
        | None -> None
    )
    // only care about the ones where we have an actual title
    // this unwraps the option and filters out the Nones
    |> Seq.choose(id)
    // and parse the body to extract functions, enums, and typedefs
    |> Seq.map (fun (blockStart, blockLength, title) ->
        let body = source.Substring(blockStart, blockLength)
        let nativeFunctions = body |> parseNativeFunctions |> Seq.toList
        let nativeEnums = body |> parseNativeEnumerations |> Seq.toList
        let typeDefs = body |> parseNativeTypeDefs |> Seq.toList
        {
            title = title
            nativeFunctions = nativeFunctions
            nativeEnums = nativeEnums
            nativeTypeDefs = typeDefs
        }
    )

let headerUri = sprintf "https://raw.githubusercontent.com/facebook/rocksdb/v%s/include/rocksdb/c.h" rocksdbVersion
let cLang =
    (download headerUri)
    |> cleanupLineEndings

let regions =
    cLang
    |> parseRocksHeaderFileRegions
    |> Seq.toList

eprintf "Regions: %d" (List.length regions)
for r in regions
    do eprintfn "region: %A" r

