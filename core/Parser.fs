// Parser.fs — 订阅链接解析与节点模型（纯函数，无 IO）
// 设计要点：record + 内部 DU 建模；解析失败统一返回 option；输入不可信，catch-all → None 是刻意例外
namespace Sub2Clash.Core

#nowarn "3261" // 与 .NET BCL 互操作边界上的 null 噪音

open System
open System.Text
open System.Text.Json.Nodes

// ─── 领域模型 ───

type WsOpt = { Path: string; Host: string option }

type Proxy =
    { Name: string
      Server: string
      Port: int
      Udp: bool
      Kind: Kind
      Tls: bool
      ServerName: string option
      Flow: string option
      Fingerprint: string option
      Alpn: string list option
      Reality: (string * string) option // public-key * short-id
      Ws: WsOpt option
      Grpc: string option
      Http: string list option }

and Kind =
    | Ss of Cipher: string * Password: string
    | Vless of Uuid: string * Network: string
    | Vmess of Uuid: string * AlterId: int * Cipher: string * Network: string
    | Trojan of Password: string * Sni: string * Network: string

module Proxy =
    let internal yq (s: string) =
        if String.IsNullOrEmpty s
           || s |> Seq.exists (fun c -> not (Char.IsAsciiLetterOrDigit c || c = '.' || c = '_' || c = '/' || c = '-')) then
            "\"" + s.Replace("\"", "\\\"") + "\""
        else
            s

    let private kindTag =
        function
        | Ss _ -> "ss"
        | Vless _ -> "vless"
        | Vmess _ -> "vmess"
        | Trojan _ -> "trojan"

    /// 输出 mihomo YAML 节点片段（2 空格缩进，与 proxy-groups 同级）
    let yaml (p: Proxy) : string =
        [ yield $"  - name: {yq p.Name}"
          yield $"    type: {kindTag p.Kind}"
          yield $"    server: {p.Server}"
          yield $"    port: {p.Port}"
          match p.Kind with
          | Ss(cipher, password) ->
              yield $"    cipher: {cipher}"
              yield $"    password: {password}"
          | Vless(uuid, net) ->
              yield $"    uuid: {uuid}"
              yield $"    network: {net}"
          | Vmess(uuid, alterId, cipher, net) ->
              yield $"    uuid: {uuid}"
              yield $"    alterId: {alterId}"
              yield $"    cipher: {cipher}"
              yield $"    network: {net}"
          | Trojan(password, sni, net) ->
              yield $"    password: {password}"
              yield $"    sni: {sni}"
              yield $"    network: {net}"
          if p.Udp then yield "    udp: true"
          if p.Tls then yield "    tls: true"
          match p.ServerName with
          | Some s -> yield $"    servername: {s}"
          | None -> ()
          match p.Flow with
          | Some s -> yield $"    flow: {s}"
          | None -> ()
          match p.Fingerprint with
          | Some s -> yield $"    client-fingerprint: {s}"
          | None -> ()
          match p.Alpn with
          | Some alpn ->
              yield "    alpn:"
              yield! alpn |> List.map (fun a -> $"      - {a}")
          | None -> ()
          match p.Reality with
          | Some(pk, sid) ->
              yield "    reality-opts:"
              yield $"      public-key: {pk}"
              yield $"      short-id: {sid}"
          | None -> ()
          match p.Ws with
          | Some ws ->
              yield "    ws-opts:"
              yield $"      path: {yq ws.Path}"
              match ws.Host with
              | Some h ->
                  yield "      headers:"
                  yield $"        Host: {h}"
              | None -> ()
          | None -> ()
          match p.Grpc with
          | Some name ->
              yield "    grpc-opts:"
              yield $"      grpc-service-name: {name}"
          | None -> ()
          match p.Http with
          | Some paths ->
              yield "    http-opts:"
              yield "      method: GET"
              yield "      path:"
              yield! paths |> List.map (fun x -> $"        - {yq x}")
          | None -> () ]
        |> String.concat "\n"

module Parser =
    // F# 10 无内置 option CE，自建最小构建器（大项目可改用 FsToolkit.ErrorHandling）
    type private OptionBuilder() =
        member _.Bind(x, f) = Option.bind f x
        member _.Return(x) = Some x
        member _.ReturnFrom(x) = x

    let private option = OptionBuilder()

    let (|Prefix|_|) (p: string) (s: string) =
        if s.StartsWith(p, StringComparison.Ordinal) then Some(s[p.Length..]) else None

    let internal b64 (s: string) : string option =
        try
            let t = s.Replace('-', '+').Replace('_', '/').Trim()
            let t = t.PadRight(t.Length + (4 - t.Length % 4) % 4, '=')
            Convert.FromBase64String t |> Encoding.UTF8.GetString |> Some
        with _ ->
            None

    let private dec (s: string) =
        try Uri.UnescapeDataString s
        with _ -> s

    let private splitFrag (s: string) =
        match s.IndexOf '#' with
        | -1 -> s, ""
        | h -> s[..h - 1], dec s[(h + 1)..]

    let private query (s: string) : Map<string, string> =
        s.Split('&', StringSplitOptions.RemoveEmptyEntries)
        |> Array.choose (fun kv ->
            match kv.Split('=', 2) with
            | [| k; v |] -> Some(dec k, dec (v.Replace('+', ' ')))
            | _ -> None)
        |> Map.ofArray

    let private hostPort (hp: string) =
        let port (s: string) =
            match Int32.TryParse s with
            | true, v -> v
            | _ -> 443

        if hp.StartsWith '[' then
            match hp.IndexOf ']' with
            | -1 -> hp, 443
            | e -> hp[1..e - 1], port (if e + 1 < hp.Length then hp[(e + 1)..].TrimStart ':' else "")
        else
            match hp.LastIndexOf ':' with
            | -1 -> hp, 443
            | i -> hp[..i - 1], port hp[(i + 1)..]

    let private baseProxy name server port kind =
        { Name = name
          Server = server
          Port = port
          Udp = true
          Kind = kind
          Tls = false
          ServerName = None
          Flow = None
          Fingerprint = None
          Alpn = None
          Reality = None
          Ws = None
          Grpc = None
          Http = None }

    let private parseSs (rest: string) : Proxy option =
        option {
            let rest, name = splitFrag rest
            let! server, port, cipher, password =
                if rest.Contains '@' then
                    let at = rest.LastIndexOf '@'
                    let ui =
                        let u = rest[..at - 1]
                        if u.Contains ':' then u else defaultArg (b64 u) u
                    let ci = ui.IndexOf ':'
                    if ci < 0 then
                        None
                    else
                        // 丢弃 plugin 参数（/?plugin=...），mihomo 不支持
                        let hp = rest[(at + 1)..]
                        let hp = if hp.Contains '/' then hp[..hp.IndexOf '/' - 1] else hp
                        let server, port = hostPort hp
                        Some(server, port, ui[..ci - 1], ui[(ci + 1)..])
                else
                    // 旧格式: ss://base64(method:pass@server:port)
                    b64 rest
                    |> Option.bind (fun d ->
                        let at = d.LastIndexOf '@'
                        let ci = d.IndexOf ':'
                        if at < 0 || ci < 0 || ci > at then
                            None
                        else
                            let server, port = hostPort d[(at + 1)..]
                            Some(server, port, d[..ci - 1], d[(ci + 1)..at - 1]))
            if server = "" || port = 0 then
                return! None
            else
                return baseProxy name server port (Ss(cipher, password))
        }

    let private parseVless (rest: string) : Proxy option =
        option {
            let u = Uri("vless://" + rest)
            let q = query (u.Query.TrimStart '?')
            let sec = defaultArg (Map.tryFind "security" q) "none"
            // 只认 ws/grpc/h2/http；xhttp 等新传输暂不支持，会降级为 tcp（TODO）
            let net =
                match Option.orElse (Map.tryFind "type" q) (Map.tryFind "network" q) with
                | Some n when n = "ws" || n = "grpc" || n = "h2" || n = "http" -> n
                | _ -> "tcp"
            return
                { baseProxy (dec (u.Fragment.TrimStart '#')) u.Host (if u.Port > 0 then u.Port else 443) (Vless(u.UserInfo, net)) with
                    Tls = sec = "tls" || sec = "reality"
                    ServerName = Map.tryFind "sni" q
                    Flow = Map.tryFind "flow" q
                    Fingerprint = Map.tryFind "fp" q
                    Alpn = Map.tryFind "alpn" q |> Option.map (fun a -> a.Split ',' |> Array.toList)
                    Reality =
                        if sec = "reality" then
                            Option.map2 (fun pk sid -> pk, sid) (Map.tryFind "pbk" q) (Map.tryFind "sid" q)
                        else
                            None
                    Ws = if net = "ws" then Some { Path = defaultArg (Map.tryFind "path" q) "/"; Host = Map.tryFind "host" q } else None
                    Grpc = if net = "grpc" then Option.orElse (Map.tryFind "serviceName" q) (Map.tryFind "path" q) else None
                    Http = if net = "http" then Some((defaultArg (Map.tryFind "path" q) "/").Split ',' |> Array.toList) else None }
        }

    let private parseVmess (rest: string) : Proxy option =
        option {
            let rest, name = splitFrag rest
            let! json = b64 rest
            let node = JsonNode.Parse json // 非法结构 → node 为 null 或访问抛异常 → 外层 catch → None
            let str (k: string) =
                match node[k] with
                | null -> None
                | n ->
                    (match n.AsValue().TryGetValue<string>() with
                     | true, s -> Some s
                     | _ -> None)

            let int' (k: string) d =
                match node[k] with
                | null -> d
                | n ->
                    (match n.AsValue().TryGetValue<int>() with
                     | true, i -> i
                     | _ -> d)

            let! server = str "add"
            let! id = str "id"
            let net = defaultArg (str "net") "tcp"
            return
                { baseProxy (if name <> "" then name else defaultArg (str "ps") "") server (int' "port" 443)
                            (Vmess(id, int' "aid" 0, defaultArg (str "scy") "auto", net)) with
                    Tls = str "tls" = Some "tls"
                    ServerName = str "sni"
                    Alpn = str "alpn" |> Option.map (fun a -> a.Split ',' |> Array.toList)
                    Ws = if net = "ws" then Some { Path = defaultArg (str "path") "/"; Host = str "host" } else None
                    Grpc = if net = "grpc" then str "path" else None }
        }

    let private parseTrojan (rest: string) : Proxy option =
        option {
            let u = Uri("trojan://" + rest)
            let q = query (u.Query.TrimStart '?')
            let net =
                match Option.orElse (Map.tryFind "type" q) (Map.tryFind "network" q) with
                | Some "ws" -> "ws"
                | Some "grpc" -> "grpc"
                | _ -> "tcp"
            return
                { baseProxy (dec (u.Fragment.TrimStart '#')) u.Host (if u.Port > 0 then u.Port else 443)
                            (Trojan(u.UserInfo, defaultArg (Option.orElse (Map.tryFind "sni" q) (Map.tryFind "peer" q)) u.Host, net)) with
                    Fingerprint = Map.tryFind "fp" q
                    Ws = if net = "ws" then Some { Path = defaultArg (Map.tryFind "path" q) "/"; Host = Map.tryFind "host" q } else None
                    Grpc = if net = "grpc" then Map.tryFind "serviceName" q else None }
        }

    /// 解析一行链接；未知协议或任何解析异常 → None（订阅内容不可信，catch-all 是刻意为之）
    let parse (line: string) : Proxy option =
        try
            match line with
            | Prefix "ss://" rest -> parseSs rest
            | Prefix "vless://" rest -> parseVless rest
            | Prefix "vmess://" rest -> parseVmess rest
            | Prefix "trojan://" rest -> parseTrojan rest
            | _ -> None
        with _ ->
            None
