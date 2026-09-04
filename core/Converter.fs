// Converter.fs — 订阅更新管道（IO 在此：HTTP、文件、Docker、命名管道；纯函数在 Parser.fs）
// helper 放进 [<AutoOpen>] 私有模块；HttpClient 等有副作用的依赖放 UpdateService 构造函数里
namespace Sub2Clash.Core

#nowarn "3261"

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.IO.Pipes
open System.Net.Http
open System.Text
open System.Text.RegularExpressions
open System.Threading.Tasks
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

[<AllowNullLiteral>]
type SubDoc() =
    member val rules: List<string> = null with get, set

[<AutoOpen>]
module private Helpers =
    // ACL4SSR 组名 → 我们的 3 组映射
    let groupMap =
        [ "🎯 全球直连", "DIRECT"; "🌏 国内媒体", "DIRECT"; "📺 哔哩哔哩", "DIRECT"
          "Ⓜ️ 微软Bing", "DIRECT"; "Ⓜ️ 微软云盘", "DIRECT"; "Ⓜ️ 微软服务", "DIRECT"
          "🍎 苹果服务", "DIRECT"; "🎮 游戏平台", "DIRECT"; "🎶 网易音乐", "DIRECT"
          "📢 谷歌FCM", "DIRECT"
          "🛑 广告拦截", "REJECT"; "🍃 应用净化", "REJECT"; "🛑 全球拦截", "REJECT"
          "🚀 节点选择", "🔰 手动选择"; "🔰 节点选择", "🔰 手动选择"; "♻️ 自动选择", "♻️ 自动选择"
          "🐟 漏网之鱼", "🔰 手动选择"; "📲 电报消息", "🔰 手动选择"; "📲 电报信息", "🔰 手动选择"
          "💬 Ai平台", "🔰 手动选择"; "📹 油管视频", "🔰 手动选择"
          "🎥 奈飞视频", "🔰 手动选择"; "🎥 NETFLIX", "🔰 手动选择"; "📺 巴哈姆特", "🔰 手动选择"
          "🌍 国外媒体", "🔰 手动选择"; "🎥 奈飞节点", "🔰 手动选择" ]
        |> Map.ofList

    let validGroups = Set.ofList [ "DIRECT"; "REJECT"; "🔰 手动选择"; "♻️ 自动选择"; "🛑 广告拦截" ]

    let mask (link: string) =
        let i = link.IndexOf '?'
        if i < 0 then link else link[..i] + "?***"

    let truncate (s: string) (n: int) = if s.Length <= n then s else s[..n]

    let remapRule (rule: string) =
        let parts = rule.Split ','
        let mutable changed = 0

        for i in 0 .. parts.Length - 1 do
            if changed = 0 then
                let f = parts[i].Trim()

                match Map.tryFind f groupMap with
                | Some mapped ->
                    parts[i] <- mapped
                    changed <- 1
                | None ->
                    let isGroupLike =
                        f.Length > 1
                        && not (Set.contains f validGroups)
                        && (f |> Seq.exists (fun c -> c >= '\u4e00' && c <= '\u9fff')
                            || f |> Seq.exists Char.IsSurrogate)

                    if isGroupLike then
                        parts[i] <- "🔰 手动选择"
                        changed <- 1

        String.concat "," parts, changed

    let runProc (exe: string) (args: string) =
        use p = new Process()
        p.StartInfo.FileName <- exe
        p.StartInfo.Arguments <- args
        p.StartInfo.CreateNoWindow <- true
        p.StartInfo.UseShellExecute <- false
        if p.Start() then
            p.WaitForExit 30000 |> ignore
            if p.HasExited then p.ExitCode else -1
        else
            -1

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
    let subApi = "http://127.0.0.1:25500"
    let profUid = "sub2clash"

    // HttpClient 放构造函数而非模块级 let：初始化异常不会变成 TypeInitializationException
    let http =
        let c = new HttpClient()
        c.Timeout <- TimeSpan.FromSeconds 30.0
        c.DefaultRequestHeaders.UserAgent.ParseAdd "clash-verge/2.0.0"
        c

    // 127.0.0.1:25500 走系统代理（Clash）会 502，subconverter 请求强制直连
    let httpNoProxy =
        let c = new HttpClient(new HttpClientHandler(UseProxy = false, Proxy = null))
        c.Timeout <- TimeSpan.FromSeconds 30.0
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

    member private _.ensureSubconverter () =
        task {
            try
                use! _ = httpNoProxy.GetAsync $"{subApi}/version"
                return ()
            with _ ->
                let start = runProc "docker" "start subconverter"
                let created = runProc "docker" "run -d --name subconverter --restart=always -p 25500:25500 tindy2013/subconverter:latest"
                if start <> 0 && created <> 0 then
                    invalidOp "Docker 启动失败"
                do! Task.Delay 3000
        }

    member private _.fetchSub (url: string) =
        task {
            let mutable body = ""
            for _ in 1..3 do
                if body = "" then
                    try
                        use! rsp = httpNoProxy.GetAsync url
                        if rsp.IsSuccessStatusCode then
                            let! t = rsp.Content.ReadAsStringAsync()
                            body <- t
                    with _ ->
                        ()
                    if body = "" then
                        do! Task.Delay 2000
            return body
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

            // 2. 规则：rules.txt = none → 内置极简规则；否则 subconverter 生成
            let rulesFile = Path.Combine(root, "rules.txt")
            let mutable ruleConfig = "/base/config/ACL4SSR_Online_Mini.ini"
            let mutable useConfig = true

            if File.Exists rulesFile then
                let r =
                    File.ReadLines rulesFile
                    |> Seq.tryHead
                    |> Option.defaultValue ""
                    |> _.Trim()

                if r.Length > 0 && not (r.StartsWith '#') then
                    if r = "none" || r = "off" then
                        useConfig <- false
                        ruleConfig <- "none"
                    else
                        ruleConfig <- if r.StartsWith '/' || r.StartsWith "http" then r else "/base/config/" + r

            let mutable rules: string list = []
            let mutable remapped = 0

            if useConfig then
                log $"══════ subconverter（规则: {ruleConfig.Split('/') |> Array.last}）══════"
                do! this.ensureSubconverter ()
                let merged = String.concat "|" links
                let url =
                    $"{subApi}/sub?target=clash&url={Uri.EscapeDataString merged}&config={ruleConfig}&insert=true"
                let! body = this.fetchSub url
                if body <> "" then
                    try
                        let des = DeserializerBuilder().IgnoreUnmatchedProperties().Build()
                        let doc = des.Deserialize<SubDoc> body
                        match doc.rules with
                        | null -> ()
                        | rs ->
                            for r in rs do
                                let r', c = remapRule r
                                remapped <- remapped + c
                                rules <- r' :: rules
                            rules <- List.rev rules
                    with _ ->
                        log "[!] subconverter 返回无效"
                else
                    log "[!] subconverter 3次重试均失败"

                if rules.IsEmpty then log "[!] 外置规则不可用，回退到内置规则"

            if rules.IsEmpty then rules <- [ "GEOIP,CN,DIRECT"; "MATCH,🔰 手动选择" ]

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

            log $"[√] {names.Length} 节点 | {rules.Length} 规则 | remap {remapped} 条"

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
