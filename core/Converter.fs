// Converter.fs — 订阅更新管道（IO 在此：HTTP、文件、Docker、命名管道；纯函数在 Parser.fs）
// helper 放进 [<AutoOpen>] 私有模块；HttpClient 等有副作用的依赖放 UpdateService 构造函数里
namespace Sub2Clash.Core

#nowarn "3261"

open System
open System.Collections.Generic
open System.IO
open System.IO.Pipes
open System.Net.Http
open System.Text
open System.Text.RegularExpressions
open Parser
open YamlDotNet.Serialization

/// 供 C# 托盘/--once 模式消费的结果
type UpdateResult = { Ok: bool; Summary: string }

// profiles.yaml 的外部文件形状（YamlDotNet 需要可变 POCO；Nullable 避免 option 被序列化成 "Some(...)"）
[<AllowNullLiteral>]
type ProfileOption() =
    member val update_interval = Nullable<int64>() with get, set
    member val allow_auto_update = Nullable<bool>() with get, set

[<AllowNullLiteral>]
type ProfileItem() =
    member val uid: string = null with get, set
    member val ``type``: string = null with get, set
    member val name: string = null with get, set
    member val file: string = null with get, set
    member val desc: string = null with get, set
    member val updated = Nullable<int64>() with get, set
    member val option: ProfileOption = null with get, set
    member val selected: List<Dictionary<string, string>> = null with get, set

[<AllowNullLiteral>]
type ProfileData() =
    member val current: string = null with get, set
    member val items: List<ProfileItem> = null with get, set

[<AutoOpen>]
module private Helpers =
    let mask (link: string) =
        let i = link.IndexOf '?'
        if i < 0 then link else link.Substring(0, i) + "?***"

    let truncate (s: string) (n: int) = if s.Length <= n then s else s[..n]

    let reload () =
        try
            use pipe = new NamedPipeClientStream(".", "verge-mihomo", PipeDirection.InOut)
            try
                pipe.Connect 5000
                let body = Encoding.UTF8.GetBytes "{\"path\":\"\"}"
                pipe.Write(
                    Encoding.UTF8.GetBytes
                        $"PUT /configs?force=true HTTP/1.1\r\nHost: localhost\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"
                )
                pipe.Write body
                pipe.Flush()
                use sr = new StreamReader(pipe, Encoding.UTF8)
                let resp = sr.ReadToEnd()
                if resp.Contains "200" || resp.Contains "204" then "OK" else resp
            with _ ->
                "ERR:connect_timeout"
        with e ->
            "ERR:" + e.Message

    let updateProfilesYaml (path: string) (text: string) (uid: string) =
        let des = DeserializerBuilder().IgnoreUnmatchedProperties().Build()
        let data =
            match des.Deserialize<ProfileData> text with
            | null -> ProfileData()
            | d -> d

        let ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        let items =
            match data.items with
            | null -> List<ProfileItem>()
            | l -> l

        items.RemoveAll(fun i -> i.uid = "direct-rules") |> ignore

        match items |> Seq.tryFind (fun i -> i.uid = uid) with
        | Some item ->
            let o = ProfileOption()
            o.update_interval <- Nullable 1440L
            o.allow_auto_update <- Nullable true
            item.option <- o
            item.updated <- Nullable ts
        | None ->
            data.current <- uid
            let it = ProfileItem()
            it.uid <- uid
            it.``type`` <- "local"
            it.name <- "sub2clash"
            it.file <- uid + ".yaml"
            it.desc <- ""
            it.updated <- Nullable ts
            let o = ProfileOption()
            o.update_interval <- Nullable 1440L
            o.allow_auto_update <- Nullable true
            it.option <- o
            items.Add it

        data.items <- items
        let ser = SerializerBuilder().ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull).Build()
        File.WriteAllText(path, ser.Serialize data, UTF8Encoding false)

// ─── 更新服务 ───

type UpdateService() =
    let profUid = "sub2clash"

    // HttpClient 放构造函数而非模块级 let：初始化异常不会变成 TypeInitializationException
    let http =
        let c = new HttpClient()
        c.Timeout <- TimeSpan.FromSeconds 30.0
        c.DefaultRequestHeaders.UserAgent.ParseAdd "clash-verge/2.0.0"
        c

    member private _.fetchLines (url: string) =
        task {
            try
                use! rsp = http.GetAsync url
                rsp.EnsureSuccessStatusCode() |> ignore
                let! body = rsp.Content.ReadAsStringAsync()
                let text = body.Trim()
                let text =
                    if text.Length > 0 && not (text.Contains "://") then
                        match b64 (text |> Seq.filter (fun c -> not (Char.IsWhiteSpace c)) |> String.Concat) with
                        | Some d when d.Contains "://" -> d
                        | _ -> text
                    else
                        text
                return
                    Some(
                        text.Split('\r', '\n')
                        |> Array.map _.Trim()
                        |> Array.filter (fun s -> s.Length > 0 && not (s.StartsWith '#'))
                        |> Array.toList
                    )
            with _ ->
                return None
        }

    member private this.run (log: string -> unit) =
        task {
            let root = AppContext.BaseDirectory
            let subFile = Path.Combine(root, "sub_links.txt")
            if not (File.Exists subFile) then invalidOp "缺少 sub_links.txt"

            let links =
                File.ReadAllLines subFile
                |> Array.collect (fun l -> l.Split '|')
                |> Array.map _.Trim()
                |> Array.filter (fun l -> l.Length > 0 && not (l.StartsWith '#'))
                |> Array.toList

            if links.IsEmpty then invalidOp "sub_links.txt 里没链接"

            let directFile = Path.Combine(root, "my_direct.txt")
            let domains =
                if File.Exists directFile then
                    File.ReadAllLines directFile
                    |> Array.map _.Trim()
                    |> Array.filter (fun l -> l.Length > 0 && not (l.StartsWith '#') && not (l |> Seq.exists Char.IsWhiteSpace))
                    |> Array.toList
                else
                    []

            // 1. 拉取 + 解析
            log "══════ 拉取并解析订阅 ══════"
            let seen = HashSet<string>()
            let proxies = ResizeArray<Proxy>()
            let mutable unsupported = 0
            let mutable fetchFail = 0

            let kindName =
                function
                | Ss _ -> "ss"
                | Vless _ -> "vless"
                | Vmess _ -> "vmess"
                | Trojan _ -> "trojan"

            let addLines (lines: string list) =
                for ln in lines do
                    match Parser.parse ln with
                    | None -> if ln.Contains "://" then unsupported <- unsupported + 1
                    | Some p ->
                        let baseName =
                            if p.Name = "" then $"{kindName p.Kind} {p.Server}:{p.Port}" else p.Name

                        let rec pick i =
                            let name = if i = 0 then baseName else $"{baseName} #{i + 1}"
                            if seen.Add name then name else pick (i + 1)

                        proxies.Add { p with Name = pick 0 }

            for link in links do
                if link.StartsWith "http://" || link.StartsWith "https://" then
                    match! this.fetchLines link with
                    | Some lines -> addLines lines
                    | None ->
                        log $"[!] 拉取失败 {mask link}"
                        fetchFail <- fetchFail + 1
                else
                    addLines [ link ]

            log $"  原生解析 {proxies.Count} 个节点 | 不支持 {unsupported} 个 | 拉取失败 {fetchFail} 个"
            if proxies.Count = 0 then invalidOp "没有解析到任何节点"

            // 2. 规则：本地生成，用 Clash Verge 自带的 geosite 数据，无需 Docker/subconverter
            //    rules.txt = none/off → 极简；其他（含不写）→ cn 模式（广告拦截 + 国内直连）
            let rulesFile = Path.Combine(root, "rules.txt")
            let mode =
                if File.Exists rulesFile then
                    match File.ReadLines rulesFile |> Seq.tryHead with
                    | Some r when r.Trim() = "none" || r.Trim() = "off" -> "none"
                    | _ -> "cn"
                else
                    "cn"

            let mutable rules =
                if mode = "none" then
                    [ "GEOIP,CN,DIRECT"; "MATCH,🔰 手动选择" ]
                else
                    [ "GEOSITE,category-ads-all,REJECT"
                      "GEOSITE,cn,DIRECT"
                      "GEOIP,CN,DIRECT"
                      "MATCH,🔰 手动选择" ]

            log $"  规则: {mode}（本地生成）"

            // 3. 自定义直连域名 + 组装 YAML
            for d in domains do
                rules <- $"DOMAIN-SUFFIX,{d},DIRECT" :: $"DOMAIN,{d},DIRECT" :: rules

            let names = proxies |> Seq.map (fun p -> p.Name) |> Seq.toList

            let yamlLines =
                [ yield "proxies:"
                  for p in proxies do
                      yield Proxy.yaml p
                  yield "rules:"
                  for r in rules do
                      yield $"  - {r}"
                  yield "proxy-groups:"
                  yield "  - name: 🔰 手动选择"
                  yield "    type: select"
                  yield "    proxies:"
                  yield "      - ♻️ 自动选择"
                  yield "      - DIRECT"
                  for n in names do
                      yield $"      - {Proxy.yq n}"
                  yield "  - name: ♻️ 自动选择"
                  yield "    type: url-test"
                  yield "    proxies:"
                  for n in names do
                      yield $"      - {Proxy.yq n}"
                  yield "    url: http://www.gstatic.com/generate_204"
                  yield "    interval: 300"
                  yield "  - name: 🛑 广告拦截"
                  yield "    type: select"
                  yield "    proxies:"
                  yield "      - REJECT"
                  yield "      - DIRECT"
                  yield "mixed-port: 7897"
                  yield "mode: rule" ]

            log $"[√] {names.Length} 节点 | {rules.Length} 规则"

            // 4. 写入 Clash Verge
            let clashDir =
                match Environment.GetEnvironmentVariable "SUBCLASH_CLASH_DIR" with
                | null ->
                    Path.Combine(
                        Environment.GetFolderPath Environment.SpecialFolder.ApplicationData,
                        "io.github.clash-verge-rev.clash-verge-rev"
                    )
                | dir -> dir

            if not (Directory.Exists clashDir) then invalidOp "没装 Clash Verge"
            let profilesDir = Path.Combine(clashDir, "profiles")
            Directory.CreateDirectory profilesDir |> ignore

            let profileYamlPath = Path.Combine(clashDir, "profiles.yaml")
            let pyText = if File.Exists profileYamlPath then File.ReadAllText profileYamlPath else ""

            let uid =
                if pyText = "" then
                    profUid
                else
                    let m = Regex.Match(pyText, @"current:\s*(\S+)")
                    if m.Success && m.Groups[1].Value <> "null" then m.Groups[1].Value else profUid

            let pf = Path.Combine(profilesDir, uid + ".yaml")
            if File.Exists pf then File.Copy(pf, pf + ".bak", true)
            File.WriteAllText(pf, "# Clash Verge | sub2clash\n\n" + (String.concat "\n" yamlLines) + "\n", UTF8Encoding false)
            File.Copy(pf, Path.Combine(clashDir, "clash-verge.yaml"), true)

            if pyText <> "" then updateProfilesYaml profileYamlPath pyText uid

            // 5. 热重载（命名管道）
            let reloaded = reload ()
            if reloaded = "OK" then log "[√] 热重载成功" else log $"[!] {truncate reloaded 80}"

            let summary = $"完成! {links.Length}订阅 | {names.Length}节点 | {rules.Length}规则"
            log summary
            return { Ok = true; Summary = summary }
        }

    /// C# 入口：同步执行整条管道，异常在此兜底转成结果
    member this.Run(log: Action<string> | null) : UpdateResult =
        let logf (s: string) =
            if not (isNull log) then log.Invoke s

        try
            (this.run logf).GetAwaiter().GetResult()
        with e ->
            logf $"[×] {e.Message}"
            { Ok = false; Summary = e.Message }
