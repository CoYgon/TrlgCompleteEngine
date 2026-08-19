using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace TrlgCompleteEngine
{
    // ==========================================
    // 1. NATIVE SQLITE3 C API P/INVOKE BINDINGS (AOT Ready)
    // ==========================================
    public static partial class NativeSqlite
    {
        private const string SqliteLib = "sqlite3";

        public const int SQLITE_OK = 0;
        public const int SQLITE_ROW = 100;
        public const int SQLITE_DONE = 101;

        // SQLITE_TRANSIENT (-1): sqlite3'e string'i KENDI kopyalamasini soyler.
        // IntPtr.Zero (SQLITE_STATIC) kullanmak, P/Invoke'un gecici marshall ettigi
        // buffer serbest kaldiktan sonra sqlite3'un o bellegi okumasina (bozuk veri/crash) yol acardi.
        public static readonly IntPtr SQLITE_TRANSIENT = new IntPtr(-1);

        [LibraryImport(SqliteLib, EntryPoint = "sqlite3_open", StringMarshalling = StringMarshalling.Utf8)]
        public static partial int Open(string filename, out IntPtr db);

        [LibraryImport(SqliteLib, EntryPoint = "sqlite3_close")]
        public static partial int Close(IntPtr db);

        [LibraryImport(SqliteLib, EntryPoint = "sqlite3_prepare_v2", StringMarshalling = StringMarshalling.Utf8)]
        public static partial int PrepareV2(IntPtr db, string zSql, int nByte, out IntPtr ppStmt, IntPtr pzTail);

        [LibraryImport(SqliteLib, EntryPoint = "sqlite3_step")]
        public static partial int Step(IntPtr stmt);

        [LibraryImport(SqliteLib, EntryPoint = "sqlite3_finalize")]
        public static partial int Finalize(IntPtr stmt);

        [LibraryImport(SqliteLib, EntryPoint = "sqlite3_bind_text", StringMarshalling = StringMarshalling.Utf8)]
        public static partial int BindText(IntPtr stmt, int index, string value, int len, IntPtr free);

        [LibraryImport(SqliteLib, EntryPoint = "sqlite3_bind_int")]
        public static partial int BindInt(IntPtr stmt, int index, int value);

        [LibraryImport(SqliteLib, EntryPoint = "sqlite3_column_int")]
        public static partial int ColumnInt(IntPtr stmt, int col);

        [LibraryImport(SqliteLib, EntryPoint = "sqlite3_column_text")]
        public static partial IntPtr ColumnText(IntPtr stmt, int col);

        [LibraryImport(SqliteLib, EntryPoint = "sqlite3_errmsg", StringMarshalling = StringMarshalling.Utf8)]
        public static partial IntPtr ErrMsgPtr(IntPtr db);

        public static string ErrMsg(IntPtr db)
        {
            IntPtr ptr = ErrMsgPtr(db);
            return ptr == IntPtr.Zero ? string.Empty : (Marshal.PtrToStringUTF8(ptr) ?? string.Empty);
        }

        public static string GetColumnText(IntPtr stmt, int col)
        {
            IntPtr ptr = ColumnText(stmt, col);
            return ptr == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUTF8(ptr) ?? string.Empty;
        }

        public static void Exec(IntPtr db, string sql)
        {
            if (PrepareV2(db, sql, -1, out IntPtr stmt, IntPtr.Zero) == SQLITE_OK)
            {
                int rc = Step(stmt);
                if (rc != SQLITE_DONE && rc != SQLITE_ROW)
                {
                    Console.WriteLine($"[SQLite Exec Hatasi]: {ErrMsg(db)}");
                }
                Finalize(stmt);
            }
            else
            {
                Console.WriteLine($"[SQLite Prepare Hatasi]: {ErrMsg(db)}");
            }
        }
    }

    // ==========================================
    // 1b. TEK BAGLANTILI, THREAD-SAFE SQLITE ERISIM KATMANI
    // ==========================================
    // FIX: Onceki kodda her istekte ayri Open/Close yapiliyordu; bu, yogun trafikte
    // "database is locked" hatalarina yol acar. Artik TEK bir baglanti aciliyor ve
    // tum erisimler bir lock ile senkronize ediliyor (SQLite native kutuphanesinin
    // derleme modundan bagimsiz olarak guvenli hale gelir).
    public static class Db
    {
        private static IntPtr _handle = IntPtr.Zero;
        private static readonly object _lock = new object();
        private const string DbPath = "trlg_app.db";

        public static void Init()
        {
            lock (_lock)
            {
                if (NativeSqlite.Open(DbPath, out _handle) != NativeSqlite.SQLITE_OK)
                {
                    Console.WriteLine("[Veritabani Acilamadi] sqlite3 native kutuphanesi bulunamamis olabilir (sqlite3.dll/.so eksik).");
                    return;
                }

                NativeSqlite.Exec(_handle, @"
                    CREATE TABLE IF NOT EXISTS Urunler (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        adi TEXT NOT NULL,
                        fiyat TEXT NOT NULL,
                        stok INTEGER NOT NULL
                    );");

                int count = 0;
                if (NativeSqlite.PrepareV2(_handle, "SELECT COUNT(*) FROM Urunler;", -1, out IntPtr stmt, IntPtr.Zero) == NativeSqlite.SQLITE_OK)
                {
                    if (NativeSqlite.Step(stmt) == NativeSqlite.SQLITE_ROW) count = NativeSqlite.ColumnInt(stmt, 0);
                    NativeSqlite.Finalize(stmt);
                }

                if (count == 0)
                {
                    NativeSqlite.Exec(_handle, @"
                        INSERT INTO Urunler (adi, fiyat, stok) VALUES
                        ('Mekanik Oyuncu Klavyesi', '1450 TL', 12),
                        ('Optik Kablosuz Mouse', '780 TL', 0),
                        ('7.1 Surround Kulaklik', '2100 TL', 18),
                        ('RGB Mousepad XL', '350 TL', 0);");
                }
            }
        }

        public static void InsertUrun(string adi, string fiyat, int stok)
        {
            lock (_lock)
            {
                if (_handle == IntPtr.Zero) return;
                if (NativeSqlite.PrepareV2(_handle, "INSERT INTO Urunler (adi, fiyat, stok) VALUES (?, ?, ?);", -1, out IntPtr stmt, IntPtr.Zero) == NativeSqlite.SQLITE_OK)
                {
                    NativeSqlite.BindText(stmt, 1, adi, -1, NativeSqlite.SQLITE_TRANSIENT);
                    NativeSqlite.BindText(stmt, 2, fiyat, -1, NativeSqlite.SQLITE_TRANSIENT);
                    NativeSqlite.BindInt(stmt, 3, stok);
                    if (NativeSqlite.Step(stmt) != NativeSqlite.SQLITE_DONE)
                        Console.WriteLine($"[Urun Ekleme Hatasi]: {NativeSqlite.ErrMsg(_handle)}");
                    NativeSqlite.Finalize(stmt);
                }
            }
        }

        public static void UpdateUrun(int id, string adi, string fiyat, int stok)
        {
            lock (_lock)
            {
                if (_handle == IntPtr.Zero) return;
                if (NativeSqlite.PrepareV2(_handle, "UPDATE Urunler SET adi = ?, fiyat = ?, stok = ? WHERE id = ?;", -1, out IntPtr stmt, IntPtr.Zero) == NativeSqlite.SQLITE_OK)
                {
                    NativeSqlite.BindText(stmt, 1, adi, -1, NativeSqlite.SQLITE_TRANSIENT);
                    NativeSqlite.BindText(stmt, 2, fiyat, -1, NativeSqlite.SQLITE_TRANSIENT);
                    NativeSqlite.BindInt(stmt, 3, stok);
                    NativeSqlite.BindInt(stmt, 4, id);
                    if (NativeSqlite.Step(stmt) != NativeSqlite.SQLITE_DONE)
                        Console.WriteLine($"[Urun Guncelleme Hatasi]: {NativeSqlite.ErrMsg(_handle)}");
                    NativeSqlite.Finalize(stmt);
                }
            }
        }

        public static void DeleteUrun(int id)
        {
            lock (_lock)
            {
                if (_handle == IntPtr.Zero) return;
                if (NativeSqlite.PrepareV2(_handle, "DELETE FROM Urunler WHERE id = ?;", -1, out IntPtr stmt, IntPtr.Zero) == NativeSqlite.SQLITE_OK)
                {
                    NativeSqlite.BindInt(stmt, 1, id);
                    if (NativeSqlite.Step(stmt) != NativeSqlite.SQLITE_DONE)
                        Console.WriteLine($"[Urun Silme Hatasi]: {NativeSqlite.ErrMsg(_handle)}");
                    NativeSqlite.Finalize(stmt);
                }
            }
        }

        public static List<Dictionary<string, string>> GetAllUrunler()
        {
            var list = new List<Dictionary<string, string>>();
            lock (_lock)
            {
                if (_handle == IntPtr.Zero) return list;
                if (NativeSqlite.PrepareV2(_handle, "SELECT id, adi, fiyat, stok FROM Urunler ORDER BY id ASC;", -1, out IntPtr stmt, IntPtr.Zero) == NativeSqlite.SQLITE_OK)
                {
                    while (NativeSqlite.Step(stmt) == NativeSqlite.SQLITE_ROW)
                    {
                        list.Add(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["id"] = NativeSqlite.ColumnInt(stmt, 0).ToString(),
                            ["adi"] = NativeSqlite.GetColumnText(stmt, 1),
                            ["fiyat"] = NativeSqlite.GetColumnText(stmt, 2),
                            ["stok"] = NativeSqlite.ColumnInt(stmt, 3).ToString()
                        });
                    }
                    NativeSqlite.Finalize(stmt);
                }
            }
            return list;
        }

        public static Dictionary<string, string>? GetUrunById(int id)
        {
            lock (_lock)
            {
                if (_handle == IntPtr.Zero) return null;
                if (NativeSqlite.PrepareV2(_handle, "SELECT id, adi, fiyat, stok FROM Urunler WHERE id = ?;", -1, out IntPtr stmt, IntPtr.Zero) == NativeSqlite.SQLITE_OK)
                {
                    NativeSqlite.BindInt(stmt, 1, id);
                    if (NativeSqlite.Step(stmt) == NativeSqlite.SQLITE_ROW)
                    {
                        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["id"] = NativeSqlite.ColumnInt(stmt, 0).ToString(),
                            ["adi"] = NativeSqlite.GetColumnText(stmt, 1),
                            ["fiyat"] = NativeSqlite.GetColumnText(stmt, 2),
                            ["stok"] = NativeSqlite.ColumnInt(stmt, 3).ToString()
                        };
                        NativeSqlite.Finalize(stmt);
                        return result;
                    }
                    NativeSqlite.Finalize(stmt);
                }
            }
            return null;
        }
    }

    // ==========================================
    // 2. TRSS (TRLG Style Sheet) DERLEYICISI
    // ==========================================
    public partial class TrssCompiler
    {
        private static readonly Dictionary<string, string> DirectiveMap = new(StringComparer.OrdinalIgnoreCase)
        {
            ["@medya"] = "@media",
            ["ekran"] = "screen",
            [" ve "] = " and "
        };

        private static readonly Dictionary<string, string> PropertyMap = new(StringComparer.OrdinalIgnoreCase)
        {
            ["arka-plan"] = "background",
            ["renk"] = "color",
            ["genislik"] = "width",
            ["yukseklik"] = "height",
            ["ic-bosluk"] = "padding",
            ["dis-bosluk"] = "margin",
            ["kenarlik"] = "border",
            ["yuvarlama"] = "border-radius",
            ["yazi-boyutu"] = "font-size",
            ["yazi-agirligi"] = "font-weight",
            ["hizalama"] = "text-align",
            ["gorunum"] = "display",
            ["golge"] = "box-shadow",
            ["ekran-genislik"] = "max-width",
            ["azami-genislik"] = "max-width",
            ["asgari-genislik"] = "min-width",
            ["azami-yukseklik"] = "max-height",
            ["asgari-yukseklik"] = "min-height",
            ["yonelim"] = "orientation"
        };

        private static readonly Dictionary<string, string> ValueMap = new(StringComparer.OrdinalIgnoreCase)
        {
            ["merkeze"] = "center",
            ["sola"] = "left",
            ["saga"] = "right",
            ["esnek"] = "flex",
            ["satir"] = "row",
            ["sutun"] = "column",
            ["kalin"] = "bold",
            ["yok"] = "none",
            ["dikey"] = "portrait",
            ["yatay"] = "landscape"
        };

        [GeneratedRegex(@"\b(?<key>[a-zA-Z0-9_-]+)\b", RegexOptions.IgnoreCase)]
        private static partial Regex CssWordRegex();

        // FIX: url(...) ve tirnak icindeki string'ler artik kelime-degistirmeden
        // korunuyor (once yer tutucuyla cikariliyor, kelime replace sonrasi geri konuyor).
        // Eskiden bir class adi ya da url icinde "renk" gibi bir kelime gecerse
        // yanlislikla CSS property/value'ya donusturuluyordu.
        [GeneratedRegex(@"url\([^)]*\)|""[^""]*""|'[^']*'", RegexOptions.IgnoreCase)]
        private static partial Regex ProtectedSegmentRegex();

        public static string Compile(string trssContent)
        {
            var protectedSegments = new List<string>();
            string css = ProtectedSegmentRegex().Replace(trssContent, match =>
            {
                protectedSegments.Add(match.Value);
                return $"\u0003{protectedSegments.Count - 1}\u0004";
            });

            foreach (var dir in DirectiveMap)
            {
                css = css.Replace(dir.Key, dir.Value, StringComparison.OrdinalIgnoreCase);
            }

            css = CssWordRegex().Replace(css, match =>
            {
                string word = match.Value;
                if (PropertyMap.TryGetValue(word, out string? mappedProp)) return mappedProp;
                if (ValueMap.TryGetValue(word, out string? mappedVal)) return mappedVal;
                return word;
            });

            css = Regex.Replace(css, "\u0003(\\d+)\u0004", m => protectedSegments[int.Parse(m.Groups[1].Value)]);

            return css;
        }
    }

    // ==========================================
    // 3. AST DUGUM YAPISI
    // ==========================================
    public class TrlgNode
    {
        public string TagName { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;
        public Dictionary<string, string> Attributes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public List<TrlgNode> Children { get; set; } = new();
    }

    // ==========================================
    // 4. TRLG PARSER MOTORU (Source Generator Regex)
    // ==========================================
    public partial class Parser
    {
        [GeneratedRegex(@"\[(?<close>/)?(?<tag>\w+)(?<attrs>[^\]]*)\]|(?<text>[^\[]+)")]
        private static partial Regex TagPatternRegex();

        [GeneratedRegex(@"(?<key>\w+)\s*=\s*""(?<val>[^""]*)""")]
        private static partial Regex AttrPatternRegex();

        // FIX: [! ... !] artik yorum satiri olarak parse edilmeden once temizleniyor.
        [GeneratedRegex(@"\[!.*?!\]", RegexOptions.Singleline)]
        private static partial Regex CommentRegex();

        // Kendi kendine kapanan (self-closing / void) etiketler: bunlarin kapanis
        // tag'i YOKTUR, bu yuzden stack'e push edilmemeli. Onceki kod bunlari push
        // edip hic pop etmiyordu; sonrasindaki her sey yanlislikla bu tag'in child'i
        // sayilip render edilmeden kayboluyordu.
        private static readonly HashSet<string> VoidTags = new(StringComparer.OrdinalIgnoreCase)
        {
            "girdi", "stil"
        };

        public static TrlgNode ParseText(string content)
        {
            // Yorumlari temizle.
            content = CommentRegex().Replace(content, "");

            // Kacis karakterleri: \[ ve \] parser tarafindan tag baslangici/bitisi
            // olarak algilanmasin diye once gecici (kullanicinin yazamayacagi) ozel
            // karakterlere donusturuluyor; metin cikisinda geri cevriliyor.
            content = content.Replace("\\[", "\u0001").Replace("\\]", "\u0002");

            var root = new TrlgNode { TagName = "ROOT" };
            var stack = new Stack<TrlgNode>();
            stack.Push(root);

            var matches = TagPatternRegex().Matches(content);

            foreach (Match match in matches)
            {
                if (match.Groups["text"].Success)
                {
                    string textVal = match.Groups["text"].Value;
                    if (!string.IsNullOrWhiteSpace(textVal))
                    {
                        stack.Peek().Children.Add(new TrlgNode { Text = textVal });
                    }
                }
                else if (match.Groups["tag"].Success)
                {
                    bool isClose = match.Groups["close"].Success;
                    string tagName = match.Groups["tag"].Value;

                    if (isClose)
                    {
                        if (stack.Count > 1 && stack.Peek().TagName.Equals(tagName, StringComparison.OrdinalIgnoreCase))
                        {
                            stack.Pop();
                        }
                    }
                    else
                    {
                        var node = new TrlgNode { TagName = tagName };
                        string attrString = match.Groups["attrs"].Value;
                        var attrMatches = AttrPatternRegex().Matches(attrString);

                        foreach (Match attrMatch in attrMatches)
                        {
                            node.Attributes[attrMatch.Groups["key"].Value] = attrMatch.Groups["val"].Value;
                        }

                        stack.Peek().Children.Add(node);

                        if (!VoidTags.Contains(tagName))
                        {
                            stack.Push(node);
                        }
                    }
                }
            }

            return root;
        }
    }

    // ==========================================
    // 5. HTTP REQUEST MODELI
    // ==========================================
    public class SimpleHttpRequest
    {
        public string Method { get; set; } = "GET";
        public string Path { get; set; } = "/";
        public Dictionary<string, string> QueryParams { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Cookies { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public string Body { get; set; } = string.Empty;
    }

    // ==========================================
    // 5b. BASIT OTURUM (SESSION) YONETIMI
    // ==========================================
    // FIX: yeni eklendi. In-memory oturum deposu; "trlg_sid" cerezi ile eslenir.
    public static class Sessions
    {
        private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, string>> Store = new();
        public const string CookieName = "trlg_sid";

        public static (string id, ConcurrentDictionary<string, string> data, bool isNew) GetOrCreate(SimpleHttpRequest req)
        {
            if (req.Cookies.TryGetValue(CookieName, out string? sid) && Store.TryGetValue(sid, out var existing))
            {
                return (sid, existing, false);
            }

            string newId = Guid.NewGuid().ToString("N");
            var data = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            data["sepet_adet"] = "0";
            Store[newId] = data;
            return (newId, data, true);
        }
    }

    // ==========================================
    // 6. DIRECT SOCKET & NATIVE SQLITE SERVER
    // ==========================================
    public partial class Server
    {
        private const string WwwRoot = "./wwwroot";
        private const string StaticRoot = "./wwwroot/static";
        private const int Port = 8080;

        // FIX: es zamanli baglanti sayisini sinirlar; thread patlamasini onler.
        private static readonly SemaphoreSlim ConnectionLimiter = new SemaphoreSlim(64, 64);

        [GeneratedRegex(@"\{\{\w+\}\}")]
        private static partial Regex VariableCleanerRegex();

        private static readonly Dictionary<string, string> ContentTypeMap = new(StringComparer.OrdinalIgnoreCase)
        {
            [".png"] = "image/png",
            [".jpg"] = "image/jpeg",
            [".jpeg"] = "image/jpeg",
            [".gif"] = "image/gif",
            [".svg"] = "image/svg+xml",
            [".ico"] = "image/x-icon",
            [".css"] = "text/css; charset=utf-8",
            [".js"] = "application/javascript; charset=utf-8",
            [".json"] = "application/json; charset=utf-8",
            [".txt"] = "text/plain; charset=utf-8",
            [".woff"] = "font/woff",
            [".woff2"] = "font/woff2",
            [".ttf"] = "font/ttf"
        };

        public static void Main()
        {
            if (!Directory.Exists(WwwRoot)) Directory.CreateDirectory(WwwRoot);
            if (!Directory.Exists(StaticRoot)) Directory.CreateDirectory(StaticRoot);

            Db.Init();
            EnsureSamplePages();

            Socket listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listener.Bind(new IPEndPoint(IPAddress.Any, Port));
            listener.Listen(100);

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("==================================================");
            Console.WriteLine(" TRLG Zero-Dependency Native Engine v2");
            Console.WriteLine($" Port: http://localhost:{Port}/");
            Console.WriteLine(" Tekli SQLite baglantisi + lock, es zamanli baglanti siniri,");
            Console.WriteLine(" statik dosya sunumu, oturum destegi, XSS korumasi aktif.");
            Console.WriteLine("==================================================");
            Console.ResetColor();

            while (true)
            {
                try
                {
                    Socket clientSocket = listener.Accept();
                    ThreadPool.QueueUserWorkItem(state => ProcessClientSocket((Socket)state!), clientSocket);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Socket Kabul Hatasi]: {ex.Message}");
                }
            }
        }

        private static void ProcessClientSocket(Socket clientSocket)
        {
            using (clientSocket)
            {
                if (!ConnectionLimiter.Wait(2000))
                {
                    try { SendSocketResponse(clientSocket, "<h1>503 - Sunucu Mesgul</h1>", "text/html; charset=utf-8", "503 Service Unavailable", null); } catch { }
                    return;
                }

                try
                {
                    clientSocket.ReceiveTimeout = 3000;
                    clientSocket.SendTimeout = 3000;

                    byte[] buffer = new byte[8192];
                    int received = clientSocket.Receive(buffer);
                    if (received <= 0) return;

                    string rawData = Encoding.UTF8.GetString(buffer, 0, received);
                    SimpleHttpRequest request = ParseHttpRequest(rawData, clientSocket, buffer, received);

                    ProcessSocketRequest(clientSocket, request);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Istek Isleme Hatasi]: {ex.Message}");
                }
                finally
                {
                    ConnectionLimiter.Release();
                }
            }
        }

        private static SimpleHttpRequest ParseHttpRequest(string initialData, Socket socket, byte[] buffer, int initialReceived)
        {
            var req = new SimpleHttpRequest();
            string[] lines = initialData.Split("\r\n");

            if (lines.Length == 0 || string.IsNullOrWhiteSpace(lines[0])) return req;

            string[] firstLineTokens = lines[0].Split(' ');
            if (firstLineTokens.Length >= 2)
            {
                req.Method = firstLineTokens[0].ToUpperInvariant();
                string fullPath = firstLineTokens[1];

                int queryIndex = fullPath.IndexOf('?');
                if (queryIndex >= 0)
                {
                    req.Path = fullPath.Substring(0, queryIndex);
                    string queryString = fullPath.Substring(queryIndex + 1);
                    ParseKeyValuePairs(queryString, req.QueryParams);
                }
                else
                {
                    req.Path = fullPath;
                }
            }

            for (int i = 1; i < lines.Length; i++)
            {
                if (string.IsNullOrEmpty(lines[i])) break;

                int colonPos = lines[i].IndexOf(':');
                if (colonPos > 0)
                {
                    string headerName = lines[i].Substring(0, colonPos).Trim();
                    string headerVal = lines[i].Substring(colonPos + 1).Trim();
                    req.Headers[headerName] = headerVal;
                }
            }

            if (req.Headers.TryGetValue("Cookie", out string? cookieHeader))
            {
                foreach (var pair in cookieHeader.Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    var kv = pair.Split('=', 2);
                    if (kv.Length == 2) req.Cookies[kv[0].Trim()] = kv[1].Trim();
                }
            }

            if (req.Method == "POST")
            {
                int contentLength = 0;
                if (req.Headers.TryGetValue("Content-Length", out string? lenStr))
                {
                    int.TryParse(lenStr, out contentLength);
                }

                int headerByteSeparatorIndex = initialData.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                int headerBytesLength = headerByteSeparatorIndex >= 0 ? headerByteSeparatorIndex + 4 : initialReceived;
                int currentBodyBytes = initialReceived - headerBytesLength;

                MemoryStream bodyStream = new MemoryStream();
                if (currentBodyBytes > 0)
                {
                    bodyStream.Write(buffer, headerBytesLength, currentBodyBytes);
                }

                while (bodyStream.Length < contentLength)
                {
                    int bytesRead = socket.Receive(buffer);
                    if (bytesRead <= 0) break;
                    bodyStream.Write(buffer, 0, bytesRead);
                }

                req.Body = Encoding.UTF8.GetString(bodyStream.ToArray());
            }

            return req;
        }

        private static void ProcessSocketRequest(Socket socket, SimpleHttpRequest request)
        {
            string rawPath = request.Path.Trim('/');

            // --- Statik dosya sunumu (FIX: yeni eklendi) ---
            if (rawPath.StartsWith("static/", StringComparison.OrdinalIgnoreCase))
            {
                ServeStaticFile(socket, rawPath.Substring("static/".Length));
                return;
            }

            if (rawPath.EndsWith(".trss", StringComparison.OrdinalIgnoreCase))
            {
                string trssFilePath = Path.Combine(WwwRoot, rawPath);
                if (File.Exists(trssFilePath))
                {
                    string trssContent = File.ReadAllText(trssFilePath);
                    string compiledCss = TrssCompiler.Compile(trssContent);
                    SendSocketResponse(socket, compiledCss, "text/css; charset=utf-8", "200 OK", null);
                }
                else
                {
                    SendSocketResponse(socket, "/* TRSS Bulunamadi */", "text/css; charset=utf-8", "404 Not Found", null);
                }
                return;
            }

            // Oturum: her istekte cerez okunur/olusturulur.
            var (sessionId, sessionData, isNewSession) = Sessions.GetOrCreate(request);
            string? setCookieHeader = isNewSession ? $"{Sessions.CookieName}={sessionId}; Path=/; HttpOnly" : null;

            if (request.Method == "POST")
            {
                var postParams = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                ParseKeyValuePairs(request.Body, postParams);

                if (rawPath.Equals("urun-ekle", StringComparison.OrdinalIgnoreCase))
                {
                    string yeniAdi = postParams.GetValueOrDefault("adi", "Isimsiz Urun");
                    string yeniFiyat = postParams.GetValueOrDefault("fiyat", "0 TL");
                    int.TryParse(postParams.GetValueOrDefault("stok", "0"), out int yeniStok);
                    if (!yeniFiyat.EndsWith("TL", StringComparison.OrdinalIgnoreCase)) yeniFiyat += " TL";

                    Db.InsertUrun(yeniAdi, yeniFiyat, yeniStok);
                    SendSocketRedirect(socket, "/urunler", setCookieHeader);
                    return;
                }

                // FIX: yeni eklendi - guncelleme rotasi.
                if (rawPath.Equals("urun-guncelle", StringComparison.OrdinalIgnoreCase))
                {
                    int.TryParse(postParams.GetValueOrDefault("id", "0"), out int guncellenecekId);
                    string adi = postParams.GetValueOrDefault("adi", "Isimsiz Urun");
                    string fiyat = postParams.GetValueOrDefault("fiyat", "0 TL");
                    int.TryParse(postParams.GetValueOrDefault("stok", "0"), out int stok);
                    if (!fiyat.EndsWith("TL", StringComparison.OrdinalIgnoreCase)) fiyat += " TL";

                    if (guncellenecekId > 0) Db.UpdateUrun(guncellenecekId, adi, fiyat, stok);
                    SendSocketRedirect(socket, "/urunler", setCookieHeader);
                    return;
                }

                // FIX: yeni eklendi - silme rotasi.
                if (rawPath.Equals("urun-sil", StringComparison.OrdinalIgnoreCase))
                {
                    int.TryParse(postParams.GetValueOrDefault("id", "0"), out int silinecekId);
                    if (silinecekId > 0) Db.DeleteUrun(silinecekId);
                    SendSocketRedirect(socket, "/urunler", setCookieHeader);
                    return;
                }

                // FIX: yeni eklendi - basit sepet demo'su (oturum kullanimini gostermek icin).
                if (rawPath.Equals("sepet-ekle", StringComparison.OrdinalIgnoreCase))
                {
                    int.TryParse(sessionData.GetValueOrDefault("sepet_adet", "0"), out int mevcut);
                    sessionData["sepet_adet"] = (mevcut + 1).ToString();
                    SendSocketRedirect(socket, "/sepet", setCookieHeader);
                    return;
                }
            }

            var queryParams = new Dictionary<string, string>(request.QueryParams, StringComparer.OrdinalIgnoreCase);

            // Oturum verilerini render baglamina ekle (ör. {{sepet_adet}}).
            foreach (var kv in sessionData) queryParams[kv.Key] = kv.Value;

            string targetFilePath;

            if (string.IsNullOrEmpty(rawPath))
            {
                targetFilePath = Path.Combine(WwwRoot, "index.trlg");
            }
            else
            {
                string[] segments = rawPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                string directFile = Path.Combine(WwwRoot, rawPath + ".trlg");

                if (File.Exists(directFile))
                {
                    targetFilePath = directFile;
                }
                else if (segments.Length >= 2 && File.Exists(Path.Combine(WwwRoot, segments[0] + ".trlg")))
                {
                    targetFilePath = Path.Combine(WwwRoot, segments[0] + ".trlg");
                    queryParams["id"] = segments[1];
                }
                else
                {
                    targetFilePath = Path.Combine(WwwRoot, "404.trlg");
                }
            }

            if (File.Exists(targetFilePath))
            {
                string status = targetFilePath.EndsWith("404.trlg") ? "404 Not Found" : "200 OK";
                ServeTrlgPage(socket, targetFilePath, queryParams, status, setCookieHeader);
            }
            else
            {
                SendSocketResponse(socket, "<h1>404 - TRLG Sayfasi Bulunamadi</h1>", "text/html; charset=utf-8", "404 Not Found", setCookieHeader);
            }
        }

        // FIX: yeni eklendi - genel amacli statik dosya sunumu (resim, css, js, font vb.)
        private static void ServeStaticFile(Socket socket, string relativePath)
        {
            // Path traversal koruması (../ ile kok dizin disina cikmayi engeller).
            string fullPath = Path.GetFullPath(Path.Combine(StaticRoot, relativePath));
            string staticRootFull = Path.GetFullPath(StaticRoot);

            if (!fullPath.StartsWith(staticRootFull, StringComparison.Ordinal) || !File.Exists(fullPath))
            {
                SendSocketResponse(socket, "404 Not Found", "text/plain; charset=utf-8", "404 Not Found", null);
                return;
            }

            string ext = Path.GetExtension(fullPath);
            string contentType = ContentTypeMap.GetValueOrDefault(ext, "application/octet-stream");
            byte[] bytes = File.ReadAllBytes(fullPath);
            SendSocketResponseBytes(socket, bytes, contentType, "200 OK", null);
        }

        private static void ServeTrlgPage(Socket socket, string filePath, Dictionary<string, string> queryParams, string status, string? setCookieHeader)
        {
            try
            {
                string rawContent = File.ReadAllText(filePath);

                TrlgNode root = Parser.ParseText(rawContent);
                var dbData = new Dictionary<string, List<Dictionary<string, string>>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["urunler"] = Db.GetAllUrunler()
                };

                // Tekli urun sayfalari (ör. urun-duzenle?id=5) icin tek kaydi da baglama ekle.
                if (queryParams.TryGetValue("id", out string? idStr) && int.TryParse(idStr, out int tekId))
                {
                    var tek = Db.GetUrunById(tekId);
                    if (tek != null)
                    {
                        foreach (var kv in tek) queryParams[kv.Key] = kv.Value;
                    }
                }

                string bodyHtml = RenderToWeb(root, dbData, queryParams, out string? linkedTrss);

                string styleLink = !string.IsNullOrEmpty(linkedTrss)
                    ? $"<link rel='stylesheet' href='/{linkedTrss}'>"
                    : "<link rel='stylesheet' href='/stil.trss'>";

                string fullDocument = $@"<!DOCTYPE html>
<html>
<head>
    <meta charset='utf-8'>
    <meta name='viewport' content='width=device-width, initial-scale=1.0'>
    <title>TRLG Web Framework</title>
    {styleLink}
</head>
<body>
    {bodyHtml}
</body>
</html>";

                SendSocketResponse(socket, fullDocument, "text/html; charset=utf-8", status, setCookieHeader);
            }
            catch (Exception ex)
            {
                string errorHtml = $"<h1 style='color:red;'>500 - TRLG Render Hatasi</h1><p>{HtmlEncode(ex.Message)}</p>";
                SendSocketResponse(socket, errorHtml, "text/html; charset=utf-8", "500 Internal Server Error", setCookieHeader);
            }
        }

        private static string RenderToWeb(TrlgNode node, Dictionary<string, List<Dictionary<string, string>>> dbData, Dictionary<string, string> currentContext, out string? linkedTrss)
        {
            StringBuilder sb = new StringBuilder();
            linkedTrss = null;

            foreach (var child in node.Children)
            {
                string tag = child.TagName.ToLowerInvariant();

                if (tag == "stil")
                {
                    linkedTrss = child.Attributes.GetValueOrDefault("src", "stil.trss");
                }
                else if (tag == "kosul" || tag == "if" || tag == "egor")
                {
                    string rawSart = child.Attributes.GetValueOrDefault("sart", child.Attributes.GetValueOrDefault("ifade", ""));
                    string sart = ReplaceVariablesRaw(rawSart, currentContext);

                    if (EvaluateCondition(sart))
                    {
                        sb.Append(RenderToWeb(child, dbData, currentContext, out _));
                    }
                }
                else if (tag == "dongu")
                {
                    string listeAdi = child.Attributes.GetValueOrDefault("liste", "");

                    if (dbData.TryGetValue(listeAdi, out var items))
                    {
                        IEnumerable<Dictionary<string, string>> pageItems = items;

                        // FIX: yeni eklendi - sayfalama (pagination). "sayfa-boyutu" attribute'u
                        // verilirse, mevcut ?sayfa= query parametresine gore Skip/Take uygulanir.
                        string sayfaBoyutuStr = child.Attributes.GetValueOrDefault("sayfa-boyutu", "");
                        if (int.TryParse(sayfaBoyutuStr, out int sayfaBoyutu) && sayfaBoyutu > 0)
                        {
                            int.TryParse(currentContext.GetValueOrDefault("sayfa", "1"), out int sayfaNo);
                            if (sayfaNo < 1) sayfaNo = 1;
                            pageItems = items.Skip((sayfaNo - 1) * sayfaBoyutu).Take(sayfaBoyutu);
                        }

                        foreach (var item in pageItems)
                        {
                            var loopContext = new Dictionary<string, string>(currentContext, StringComparer.OrdinalIgnoreCase);
                            foreach (var kvp in item) loopContext[kvp.Key] = kvp.Value;

                            sb.Append(RenderToWeb(child, dbData, loopContext, out _));
                        }
                    }
                }
                else if (tag == "form")
                {
                    string hedef = HtmlEncode(child.Attributes.GetValueOrDefault("hedef", "#"));
                    string metod = HtmlEncode(child.Attributes.GetValueOrDefault("metod", "POST"));

                    sb.Append($"<form action='{hedef}' method='{metod}' class='trlg-form'>");
                    sb.Append(RenderToWeb(child, dbData, currentContext, out _));
                    sb.Append("</form>");
                }
                else if (tag == "girdi")
                {
                    string rawTip = child.Attributes.GetValueOrDefault("tip", "text");
                    string tip = rawTip switch
                    {
                        "onay" => "checkbox",
                        "tekli-secim" => "radio",
                        "gizli" => "hidden",
                        "sayi" => "number",
                        "eposta" => "email",
                        "sifre" => "password",
                        _ => rawTip
                    };
                    string ad = HtmlEncode(child.Attributes.GetValueOrDefault("ad", ""));
                    string yerlesim = HtmlEncode(child.Attributes.GetValueOrDefault("yerlesim", ""));
                    string deger = ReplaceVariables(child.Attributes.GetValueOrDefault("deger", ""), currentContext);
                    string seciliAttr = child.Attributes.GetValueOrDefault("secili", "") == "evet" ? "checked" : "";

                    sb.Append($"<input type='{tip}' name='{ad}' value='{deger}' placeholder='{yerlesim}' class='trlg-girdi' {seciliAttr} />");
                }
                else if (tag == "alan")
                {
                    // FIX: yeni eklendi - coklu satir metin girisi (textarea).
                    string ad = HtmlEncode(child.Attributes.GetValueOrDefault("ad", ""));
                    string yerlesim = HtmlEncode(child.Attributes.GetValueOrDefault("yerlesim", ""));
                    string deger = ReplaceVariables(child.Attributes.GetValueOrDefault("deger", ""), currentContext);
                    sb.Append($"<textarea name='{ad}' placeholder='{yerlesim}' class='trlg-girdi'>{deger}</textarea>");
                }
                else if (tag == "secim")
                {
                    // FIX: yeni eklendi - acilir liste (select). Icinde [secenek deger=""..""]Metin[/secenek] kullanilir.
                    string ad = HtmlEncode(child.Attributes.GetValueOrDefault("ad", ""));
                    sb.Append($"<select name='{ad}' class='trlg-girdi'>");
                    foreach (var opt in child.Children)
                    {
                        if (opt.TagName.Equals("secenek", StringComparison.OrdinalIgnoreCase))
                        {
                            string optDeger = HtmlEncode(ReplaceVariablesRaw(opt.Attributes.GetValueOrDefault("deger", ""), currentContext));
                            string optText = ReplaceVariables(GetNodeText(opt), currentContext);
                            sb.Append($"<option value='{optDeger}'>{optText}</option>");
                        }
                    }
                    sb.Append("</select>");
                }
                else if (tag == "baslik")
                {
                    string renk = HtmlEncode(child.Attributes.GetValueOrDefault("renk", ""));
                    string styleAttr = !string.IsNullOrEmpty(renk) ? $"style='color:{renk}'" : "";
                    string text = ReplaceVariables(GetNodeText(child), currentContext);
                    sb.Append($"<h1 class='trlg-baslik' {styleAttr}>{text}</h1>");
                }
                else if (tag == "kutu")
                {
                    string genislik = HtmlEncode(child.Attributes.GetValueOrDefault("genislik", ""));
                    string styleAttr = !string.IsNullOrEmpty(genislik) ? $"style='width:{genislik}px'" : "";
                    sb.Append($"<div class='trlg-kutu' {styleAttr}>");
                    sb.Append(RenderToWeb(child, dbData, currentContext, out _));
                    sb.Append("</div>");
                }
                else if (tag == "yazi")
                {
                    string renk = HtmlEncode(child.Attributes.GetValueOrDefault("renk", ""));
                    string styleAttr = !string.IsNullOrEmpty(renk) ? $"style='color:{renk}'" : "";
                    string text = ReplaceVariables(GetNodeText(child), currentContext);
                    sb.Append($"<p class='trlg-yazi' {styleAttr}>{text}</p>");
                }
                else if (tag == "link")
                {
                    string hedef = ReplaceVariables(child.Attributes.GetValueOrDefault("hedef", "#"), currentContext);
                    string text = ReplaceVariables(GetNodeText(child), currentContext);
                    sb.Append($"<a href='{hedef}' class='trlg-link'>{text}</a>");
                }
                else if (tag == "buton")
                {
                    string tip = HtmlEncode(child.Attributes.GetValueOrDefault("tip", "button"));
                    string text = ReplaceVariables(GetNodeText(child), currentContext);
                    sb.Append($"<button type='{tip}' class='trlg-buton'>{text}</button>");
                }
            }

            return sb.ToString();
        }

        private static bool EvaluateCondition(string expr)
        {
            if (string.IsNullOrWhiteSpace(expr)) return false;
            expr = expr.Trim();

            string[] operators = new[] { "==", "!=", ">=", "<=", ">", "<" };
            foreach (var op in operators)
            {
                if (expr.Contains(op))
                {
                    var parts = expr.Split(new[] { op }, StringSplitOptions.None);
                    if (parts.Length == 2)
                    {
                        string left = parts[0].Trim();
                        string right = parts[1].Trim();

                        if (double.TryParse(left, out double leftNum) && double.TryParse(right, out double rightNum))
                        {
                            return op switch
                            {
                                "==" => leftNum == rightNum,
                                "!=" => leftNum != rightNum,
                                ">" => leftNum > rightNum,
                                "<" => leftNum < rightNum,
                                ">=" => leftNum >= rightNum,
                                "<=" => leftNum <= rightNum,
                                _ => false
                            };
                        }

                        int cmp = string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
                        return op switch
                        {
                            "==" => cmp == 0,
                            "!=" => cmp != 0,
                            ">" => cmp > 0,
                            "<" => cmp < 0,
                            ">=" => cmp >= 0,
                            "<=" => cmp <= 0,
                            _ => false
                        };
                    }
                }
            }

            if (bool.TryParse(expr, out bool boolVal)) return boolVal;
            return !string.IsNullOrEmpty(expr) && expr != "0" && !expr.Equals("false", StringComparison.OrdinalIgnoreCase);
        }

        private static void ParseKeyValuePairs(string rawData, Dictionary<string, string> dict)
        {
            if (string.IsNullOrEmpty(rawData)) return;

            string[] pairs = rawData.Split('&');
            foreach (var pair in pairs)
            {
                string[] kv = pair.Split('=');
                if (kv.Length == 2)
                {
                    dict[WebUtility.UrlDecode(kv[0])] = WebUtility.UrlDecode(kv[1]);
                }
            }
        }

        // FIX: yeni eklendi - HTML-encode helper. Tum kullanici/DB kaynakli metinler
        // artik ekrana basilmadan once encode ediliyor (XSS koruması).
        private static string HtmlEncode(string s) => WebUtility.HtmlEncode(s ?? string.Empty);

        // Degisken degerini SADECE encode etmeden dondurur - kosul (if) ifadeleri
        // ve HTML olmayan baglamlarda (ör. option value) kullanilir.
        private static string ReplaceVariablesRaw(string text, Dictionary<string, string> context)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            foreach (var kvp in context)
            {
                text = text.Replace($"{{{{{kvp.Key}}}}}", kvp.Value);
            }
            text = VariableCleanerRegex().Replace(text, "");
            return text.Replace("\u0001", "[").Replace("\u0002", "]");
        }

        // HTML govdesine basilacak metin icin: statik metin + degisken degerleri
        // HTML-encode edilir (XSS koruması), sonra kacis karakterleri geri cevrilir.
        private static string ReplaceVariables(string text, Dictionary<string, string> context)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;

            string result = text;
            foreach (var kvp in context)
            {
                result = result.Replace($"{{{{{kvp.Key}}}}}", HtmlEncode(kvp.Value));
            }
            result = VariableCleanerRegex().Replace(result, "");
            return result.Replace("\u0001", "[").Replace("\u0002", "]");
        }

        private static string GetNodeText(TrlgNode node)
        {
            if (!string.IsNullOrEmpty(node.Text)) return node.Text.Trim();
            foreach (var child in node.Children)
            {
                if (!string.IsNullOrEmpty(child.Text)) return child.Text.Trim();
            }
            return string.Empty;
        }

        private static void SendSocketResponse(Socket socket, string body, string contentType, string status, string? setCookieHeader)
        {
            byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
            SendSocketResponseBytes(socket, bodyBytes, contentType, status, setCookieHeader);
        }

        private static void SendSocketResponseBytes(Socket socket, byte[] bodyBytes, string contentType, string status, string? setCookieHeader)
        {
            string setCookieLine = setCookieHeader != null ? $"Set-Cookie: {setCookieHeader}\r\n" : "";
            string headers = $"HTTP/1.1 {status}\r\n" +
                             $"Content-Type: {contentType}\r\n" +
                             $"Content-Length: {bodyBytes.Length}\r\n" +
                             setCookieLine +
                             "Connection: close\r\n\r\n";

            byte[] headerBytes = Encoding.UTF8.GetBytes(headers);
            socket.Send(headerBytes);
            socket.Send(bodyBytes);
        }

        private static void SendSocketRedirect(Socket socket, string redirectUrl, string? setCookieHeader)
        {
            string setCookieLine = setCookieHeader != null ? $"Set-Cookie: {setCookieHeader}\r\n" : "";
            string headers = "HTTP/1.1 302 Found\r\n" +
                             $"Location: {redirectUrl}\r\n" +
                             setCookieLine +
                             "Content-Length: 0\r\n" +
                             "Connection: close\r\n\r\n";

            socket.Send(Encoding.UTF8.GetBytes(headers));
        }

        private static void EnsureSamplePages()
        {
            File.WriteAllText(Path.Combine(WwwRoot, "stil.trss"), @"
body {
    arka-plan: #121212;
    renk: #e0e0e0;
    ic-bosluk: 30px;
    font-family: 'Segoe UI', sans-serif;
}

.trlg-baslik {
    renk: #00ff88;
    dis-bosluk: 0 0 15px 0;
}

.trlg-kutu {
    arka-plan: #1e1e1e;
    kenarlik: 1px solid #333;
    yuvarlama: 8px;
    ic-bosluk: 20px;
    dis-bosluk: 0 0 15px 0;
    golge: 0 4px 12px rgba(0,0,0,0.5);
}

.trlg-yazi {
    renk: #cccccc;
    dis-bosluk: 5px 0;
}

.trlg-link {
    renk: #00d8ff;
    dis-bosluk: 0 15px 0 0;
    text-decoration: none;
}

.trlg-form {
    gorunum: esnek;
    flex-direction: sutun;
    gap: 10px;
}

.trlg-girdi {
    arka-plan: #2a2a2a;
    renk: #ffffff;
    kenarlik: 1px solid #444;
    ic-bosluk: 10px;
    yuvarlama: 4px;
}

.trlg-buton {
    arka-plan: #00ff88;
    renk: #000000;
    yazi-agirligi: kalin;
    kenarlik: yok;
    ic-bosluk: 10px 18px;
    yuvarlama: 4px;
    cursor: pointer;
}

@medya (ekran-genislik: 600px) {
    body {
        ic-bosluk: 10px;
        arka-plan: #0a0a0a;
    }
    .trlg-kutu {
        genislik: 100% !important;
        ic-bosluk: 10px;
    }
    .trlg-baslik {
        yazi-boyutu: 20px;
    }
}");

            File.WriteAllText(Path.Combine(WwwRoot, "index.trlg"), @"
[! Bu bir yorum satiridir, tarayicida gorunmez !]
[stil src=""stil.trss""]
[baslik]TRLG Zero-Dependency Native Engine[/baslik]
[kutu genislik=""550""]
    [yazi]Motor Native SQLite3 C API P/Invoke entegrasyonu ile sifir NuGet bagimliligi seviyesine cekilmistir.[/yazi]
    [link hedef=""/urunler""]Urun Listesini Gor[/link]
    [link hedef=""/urun-ekle""]Yeni Urun Ekle[/link]
    [link hedef=""/sepet""]Sepetim (Oturum Demo)[/link]
[/kutu]");

            File.WriteAllText(Path.Combine(WwwRoot, "urun-ekle.trlg"), @"
[stil src=""stil.trss""]
[baslik renk=""#ffb703""]Yeni Urun Ekle[/baslik]
[kutu genislik=""450""]
    [form hedef=""/urun-ekle"" metod=""POST""]
        [yazi]Urun Adi:[/yazi]
        [girdi tip=""text"" ad=""adi"" yerlesim=""Ornek: Mekanik Klavye""]

        [yazi]Fiyat:[/yazi]
        [girdi tip=""text"" ad=""fiyat"" yerlesim=""Ornek: 1500 TL""]

        [yazi]Stok Adedi:[/yazi]
        [girdi tip=""sayi"" ad=""stok"" yerlesim=""10""]

        [buton tip=""submit""]Urunu Kaydet[/buton]
    [/form]
[/kutu]
[link hedef=""/urunler""]« Urun Listesine Don[/link]");

            File.WriteAllText(Path.Combine(WwwRoot, "urun-duzenle.trlg"), @"
[stil src=""stil.trss""]
[baslik renk=""#ffb703""]Urun Duzenle #{{id}}[/baslik]
[kutu genislik=""450""]
    [form hedef=""/urun-guncelle"" metod=""POST""]
        [girdi tip=""gizli"" ad=""id"" deger=""{{id}}""]

        [yazi]Urun Adi:[/yazi]
        [girdi tip=""text"" ad=""adi"" deger=""{{adi}}""]

        [yazi]Fiyat:[/yazi]
        [girdi tip=""text"" ad=""fiyat"" deger=""{{fiyat}}""]

        [yazi]Stok Adedi:[/yazi]
        [girdi tip=""sayi"" ad=""stok"" deger=""{{stok}}""]

        [buton tip=""submit""]Guncelle[/buton]
    [/form]
[/kutu]
[link hedef=""/urunler""]« Urun Listesine Don[/link]");

            File.WriteAllText(Path.Combine(WwwRoot, "urunler.trlg"), @"
[stil src=""stil.trss""]
[baslik renk=""#00d8ff""]Magaza Urun Listesi[/baslik]
[link hedef=""/urun-ekle""]+ Yeni Urun Ekle[/link]

[dongu liste=""urunler""]
    [kutu genislik=""450""]
        [yazi]Urun Kodu: #{{id}} - {{adi}}[/yazi]
        [yazi]Fiyat: {{fiyat}} | Stok: {{stok}}[/yazi]

        [kosul sart=""{{stok}} == 0""]
            [yazi renk=""#ff4d4d""]TUKENDI! Bu urun stokta kalmamistir.[/yazi]
        [/kosul]

        [link hedef=""/urun-duzenle?id={{id}}""]Duzenle[/link]

        [form hedef=""/urun-sil"" metod=""POST""]
            [girdi tip=""gizli"" ad=""id"" deger=""{{id}}""]
            [buton tip=""submit""]Sil[/buton]
        [/form]
    [/kutu]
[/dongu]

[link hedef=""/""]« Ana Sayfaya Don[/link]");

            File.WriteAllText(Path.Combine(WwwRoot, "sepet.trlg"), @"
[stil src=""stil.trss""]
[baslik renk=""#00d8ff""]Sepetim (Oturum Demo)[/baslik]
[kutu genislik=""400""]
    [yazi]Sepetindeki urun adedi: {{sepet_adet}}[/yazi]
    [form hedef=""/sepet-ekle"" metod=""POST""]
        [buton tip=""submit""]Sepete 1 Urun Ekle[/buton]
    [/form]
[/kutu]
[link hedef=""/""]« Ana Sayfaya Don[/link]");

            File.WriteAllText(Path.Combine(WwwRoot, "404.trlg"), @"
[stil src=""stil.trss""]
[baslik renk=""#ff4d4d""]404 - Sayfa Bulunamadi[/baslik]
[link hedef=""/""]Ana Sayfaya Don[/link]");
        }
    }
}
