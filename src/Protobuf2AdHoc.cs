/*
 * Protobuf2AdHoc
 * ──────────────
 * Converts Protocol Buffers schemas (.proto: proto2, proto3, editions) into AdHoc protocol descriptions (.cs).
 *
 *   dotnet Protobuf2AdHoc.dll <input file or folder> [output folder] [-I <import root>]...
 *
 * One description per .proto file: the file itself and, of the files it imports, the types it uses.
 *
 * Transformation rules
 * ─────────────────────
 * • message                     → pack (class); nested messages and enums stay nested
 * • package of the file         → the namespace of the description; a type of another package stands in
 *                                 containers named after that package: google.rpc.Status
 * • int32 / sint32, int64 / sint64 → [X] int, [X] long   (the author chose a varint: values cluster around zero)
 * • uint32, uint64              → [A] uint, [A] ulong     (an unsigned varint: values cluster at the low end)
 * • fixed32/64, sfixed32/64     → uint, ulong, int, long  (the author chose a fixed width)
 * • bytes                       → Binary[,,]
 * • repeated T                  → T[,,]         map<K, V> → Map<K, V>
 * • optional, a member of a oneof, a wrapper (google.protobuf.Int32Value …) → T?
 * • oneof                       → a nested pack of optional alternatives and one field of it
 * • group (proto2)              → a nested pack and one field of it
 * • extend                      → the fields are added to the message they extend
 * • google.protobuf.Timestamp   → DateTime      Duration → an alias of AdHoc's Duration      Empty → NoArg
 * • service                     → an interface in the connection
 * • rpc                         → (L____________, Response) Method(Request req);
 * • rpc … returns (Empty)       → a one-way call: [l____________<Request>] struct Method { }
 * • rpc with `stream`           → an actor: the items go by non-transitional branches, EndOfStream closes them
 * • /package.Service/Method     → nothing to write: the namespace, the interface of the service, the method
 * • field numbers               → nothing: they are protobuf's own wire, AdHoc sends fields in the order they are declared
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using org.unirail.adhoc;

namespace org.unirail;

public static class Protobuf2AdHoc{

    // ─────────────────────────────────────────────────────────────────────────
    //  Entry point
    // ─────────────────────────────────────────────────────────────────────────

    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        var positional = new List<string>();
        var roots      = new List<string>();
        for( var i = 0; i < args.Length; i++ )
            if( args[i] == "-I" && i + 1 < args.Length ) roots.Add(Path.GetFullPath(args[++i]));
            else if( args[i].StartsWith("-I") && 2 < args[i].Length ) roots.Add(Path.GetFullPath(args[i][2..]));
            else positional.Add(args[i]);

        if( positional.Count == 0 )
        {
            Console.Error.WriteLine("""
                                    Protobuf2AdHoc - Protocol Buffers schema → AdHoc protocol description

                                    Usage: Protobuf2AdHoc <input file or folder> [output folder] [-I <import root>]...

                                    One .cs is written per .proto file. An import is looked for in the import roots, then in
                                    the folders above the importing file, then by its name under the input folder; the
                                    well-known types of google/protobuf need no files.
                                    Exit code: 0 - every input converted, 2 - an input failed or nothing to convert.
                                    """);
            return 2;
        }

        var input  = Path.GetFullPath(positional[0]);
        var output = Path.GetFullPath(1 < positional.Count ? positional[1] : Path.Combine(Directory.GetCurrentDirectory(), "AdHoc"));

        List<string> inputs;
        string       input_root;
        if( File.Exists(input) )
        {
            inputs     = [input];
            input_root = Path.GetDirectoryName(input)!;
        }
        else if( Directory.Exists(input) )
        {
            inputs     = Directory.EnumerateFiles(input, "*.proto", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal).ToList();
            input_root = input;
        }
        else
        {
            Console.Error.WriteLine($"Not found: {input}");
            return 2;
        }

        if( inputs.Count == 0 )
        {
            Console.Error.WriteLine($"No .proto file in {input}");
            return 2;
        }

        Directory.CreateDirectory(output);

        var library  = new Library(input_root, roots);
        var projects = project_names(inputs, input_root);
        var failed   = 0;
        var skipped  = 0;

        foreach( var file in inputs )
        {
            var shown = Path.GetRelativePath(input_root, file).Replace('\\', '/');
            try
            {
                var conversion = new Conversion(library, library.load(file), projects[file]);
                File.WriteAllText(Path.Combine(output, projects[file] + ".cs"), conversion.run(), new UTF8Encoding(false));

                Console.WriteLine($"{shown,-56} -> {projects[file]}.cs{(conversion.warnings.Count == 0 ? "" : $"   ({conversion.warnings.Count} warning(s))")}");
                foreach( var w in conversion.warnings ) Console.Error.WriteLine($"    {shown}: {w}");
            }
            catch( NothingToSend e )
            {
                skipped++;
                Console.WriteLine($"{shown,-56} skipped: {e.Message}");
            }
            catch( Exception e )
            {
                // A file that cannot be read says why; anything else is a defect of the converter and shows where.
                failed++;
                Console.Error.WriteLine($"{shown,-56} FAILED: {(e is ProtoSyntaxException or IOException ? e.Message : e.ToString())}");
            }
        }

        if( skipped == inputs.Count ) Console.Error.WriteLine("Nothing to convert: no input declares a message or a service.");
        return failed == 0 && skipped < inputs.Count ? 0 : 2;
    }

    /// <summary>
    /// The name of the project of every input: the name of its file. Two files of one name in different folders
    /// (`v1/service.proto`, `v2/service.proto`) take as many folders into the name as it needs to tell them apart.
    /// </summary>
    static Dictionary<string, string> project_names(List<string> inputs, string input_root)
    {
        var names = new Dictionary<string, string>();
        var parts = inputs.ToDictionary(f => f, f => Path.ChangeExtension(Path.GetRelativePath(input_root, f), null).Split('\\', '/'));

        foreach( var f in inputs ) names[f] = project_name(parts[f][^1]);

        for( var depth = 2;; depth++ )
        {
            var repeated = names.Values.GroupBy(v => v, StringComparer.OrdinalIgnoreCase).Where(g => 1 < g.Count()).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if( repeated.Count == 0 || inputs.All(f => parts[f].Length < depth) ) break;

            foreach( var f in inputs.Where(f => repeated.Contains(names[f])) )
                names[f] = project_name(string.Join('_', parts[f].TakeLast(depth)));
        }

        // What is still not unique (two paths that differ in characters a name cannot hold) gets a number.
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach( var f in inputs )
        {
            var name = names[f];
            for( var i = 2; !taken.Add(name); i++ ) name = names[f] + i;
            names[f] = name;
        }

        return names;
    }

    static string project_name(string file_name)
    {
        var sb = new StringBuilder(file_name.Length);
        foreach( var c in file_name )
            if( char.IsAsciiLetterOrDigit(c) ) sb.Append(c);
            else if( 0 < sb.Length && sb[^1] != '_' ) sb.Append('_');

        var name = sb.ToString().Trim('_');
        if( name.Length == 0 ) name = "proto";
        if( char.IsDigit(name[0]) ) name = "N" + name;
        return AdHocNames.brush(name);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  The files a conversion can see
    // ─────────────────────────────────────────────────────────────────────────

    sealed class Library(string input_root, List<string> roots){
        readonly Dictionary<string, ProtoFile> loaded   = new(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, ProtoFile> built_in = new();
        List<string>?                          all; // every .proto under the input folder

        public ProtoFile load(string path)
        {
            path = Path.GetFullPath(path);
            if( loaded.TryGetValue(path, out var f) ) return f;
            return loaded[path] = ProtoReader.read(File.ReadAllText(path), path, Path.GetRelativePath(input_root, path).Replace('\\', '/'));
        }

        /// <summary>
        /// The file an `import` names, or null. protoc looks in its import roots only; a converter is run on
        /// whatever folder the schemas happen to lie in, so it also looks where the file is likely to be.
        /// </summary>
        public ProtoFile? import(ProtoFile from, string name)
        {
            foreach( var root in roots )
                if( File.Exists(Path.Combine(root, name)) )
                    return load(Path.Combine(root, name));

            // The folders above the importing file, nearest first: one of them is the root the import is written from.
            if( from.path != "" )
                for( var dir = Path.GetDirectoryName(from.path); dir != null; dir = Path.GetDirectoryName(dir) )
                    if( File.Exists(Path.Combine(dir, name)) )
                        return load(Path.Combine(dir, name));

            // By the end of its path, when exactly one file under the input folder has it.
            all ??= Directory.Exists(input_root) ?
                        Directory.EnumerateFiles(input_root, "*.proto", SearchOption.AllDirectories).Select(Path.GetFullPath).ToList() :
                        [];
            var tail  = "/" + name.TrimStart('/');
            var found = all.Where(p => ("/" + p.Replace('\\', '/')).EndsWith(tail, StringComparison.OrdinalIgnoreCase)).ToList();
            if( found.Count == 1 ) return load(found[0]);

            if( built_in.TryGetValue(name, out var known) ) return known;
            if( WellKnown.sources.TryGetValue(name, out var src) ) return built_in[name] = ProtoReader.read(src, "", name);
            return null;
        }
    }

    /// <summary>
    /// The well-known types of protobuf, as much of them as a conversion needs: protoc carries these files inside
    /// itself, so a project that imports them rarely has them on disk.
    /// </summary>
    static class WellKnown{
        const string HEAD = "syntax = \"proto3\"; package google.protobuf;\n";

        const string WRAPPERS = """
                                message DoubleValue { double value = 1; }
                                message FloatValue { float value = 1; }
                                message Int64Value { int64 value = 1; }
                                message UInt64Value { uint64 value = 1; }
                                message Int32Value { int32 value = 1; }
                                message UInt32Value { uint32 value = 1; }
                                message BoolValue { bool value = 1; }
                                message StringValue { string value = 1; }
                                message BytesValue { bytes value = 1; }
                                """;

        const string STRUCT = """
                              // A structured value: a map of names to dynamically typed values, as a JSON object is.
                              message Struct { map<string, Value> fields = 1; }
                              // A dynamically typed value: null, a number, a string, a boolean, a structure or a list.
                              message Value {
                                oneof kind {
                                  NullValue null_value = 1;
                                  double number_value = 2;
                                  string string_value = 3;
                                  bool bool_value = 4;
                                  Struct struct_value = 5;
                                  ListValue list_value = 6;
                                }
                              }
                              // The JSON null.
                              enum NullValue { NULL_VALUE = 0; }
                              // A list of dynamically typed values.
                              message ListValue { repeated Value values = 1; }
                              """;

        public static readonly Dictionary<string, string> sources = new()
        {
            ["google/protobuf/any.proto"]            = HEAD + "message Any { string type_url = 1; bytes value = 2; }",
            ["google/protobuf/duration.proto"]       = HEAD + "message Duration { int64 seconds = 1; int32 nanos = 2; }",
            ["google/protobuf/timestamp.proto"]      = HEAD + "message Timestamp { int64 seconds = 1; int32 nanos = 2; }",
            ["google/protobuf/empty.proto"]          = HEAD + "message Empty {}",
            ["google/protobuf/field_mask.proto"]     = HEAD + "message FieldMask { repeated string paths = 1; }",
            ["google/protobuf/source_context.proto"] = HEAD + "message SourceContext { string file_name = 1; }",
            ["google/protobuf/wrappers.proto"]       = HEAD + WRAPPERS,
            ["google/protobuf/struct.proto"]         = HEAD + STRUCT,
        };
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Names
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Types the description refers to by their simple names - of `org.unirail.Meta` and of `System`.
    /// A pack called `File` or `DateTime` would capture those references wherever it is in scope.
    /// </summary>
    static readonly string[] IMPORTED_NAMES =
    [
        "Actor", "All", "Binary", "Close", "Connects", "DateTimeDef", "Duration", "Empty", "End", "FieldsInjectInto", "File",
        "HeaderFor", "Host", "IfSendingFrom", "InCPP", "InCS", "InGO", "InJAVA", "InRS", "InTS", "Map", "Modify", "Multiplex",
        "Offline", "Resumable", "Set", "Stream", "SwapHosts", "TimeSpanDef", "VirtuallyConnects", "X", "longJS", "ulongJS",
        "Attribute", "AttributeTargets", "AttributeUsage", "DateTime", "System", "TimeSpan", "UNLIMITED",
    ];

    /// <summary>Contextual keywords C# does not accept as the name of a type (`class file` is CS9056).</summary>
    static readonly string[] NOT_A_TYPE_NAME = ["file", "required", "scoped", "record", "extension"];

    /// <summary>A name of the document as an identifier AdHoc accepts.</summary>
    static string brush(string name)
    {
        var chars = new StringBuilder(name.Length);
        foreach( var c in name ) chars.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
        name = chars.ToString().Trim('_'); // AdHoc refuses a name that starts or ends with an underscore
        if( name.Length == 0 ) name = "unnamed";
        if( char.IsDigit(name[0]) ) name = "N" + name;
        return AdHocNames.brush(name);
    }

    /// <summary>
    /// The name changed until <paramref name="taken"/> lets it through: lowercase letters go upper case, left
    /// to right; when no letter is left, a number is appended.
    /// </summary>
    static string recase(string name, Func<string, bool> taken)
    {
        if( !taken(name) ) return name;

        var chars = name.ToCharArray();
        for( var i = 0; i < chars.Length; i++ )
            if( char.IsLower(chars[i]) )
            {
                chars[i] = char.ToUpperInvariant(chars[i]);
                var candidate = new string(chars);
                if( !taken(candidate) && !AdHocNames.is_prohibited(candidate) ) return candidate;
            }

        for( var i = 2;; i++ )
            if( !taken(name + i) )
                return name + i;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Text helpers
    // ─────────────────────────────────────────────────────────────────────────

    const int DOC_WIDTH = 110;

    static string xml_escape(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;");

    static string one_line(string s) => string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    static IEnumerable<string> wrap(string line)
    {
        line = line.TrimEnd();
        var indent = line[..(line.Length - line.TrimStart().Length)];
        line = line.TrimStart();
        while( DOC_WIDTH < line.Length )
        {
            var cut = line.LastIndexOf(' ', DOC_WIDTH);
            if( cut <= 0 ) cut = line.IndexOf(' ', DOC_WIDTH);
            if( cut <= 0 ) break;
            yield return indent + line[..cut];
            line = line[(cut + 1)..].TrimStart();
        }

        yield return indent + line;
    }

    /// <summary>A documentation comment. A line that would end the comment or open another is defused.</summary>
    static string doc_comment(string? text)
    {
        if( string.IsNullOrWhiteSpace(text) ) return "";

        var lines = text.Trim('\n', '\r', ' ').Replace("\r", "").Split('\n').SelectMany(wrap).Select(l => xml_escape(l.TrimEnd())).ToArray();
        if( lines.Length == 1 ) return $"/// <summary>{lines[0].Trim()}</summary>\n";

        var sb = new StringBuilder("/// <summary>\n");
        foreach( var line in lines ) sb.Append(line.Length == 0 ? "///\n" : $"/// {line}\n");
        return sb.Append("/// </summary>\n").ToString();
    }

    /// <summary>
    /// The longest string constant written (an attribute argument is one): a longer text is cut, and a comment
    /// above it says so.
    /// </summary>
    const int MAX_CONSTANT = 1000;

    const string CUT_NOTE = "// the text below is cut at 1000 characters, the .proto has all of it\n";

    /// <summary>
    /// A string constant of the description. Text is written as it is, in a verbatim literal; a value with control
    /// characters in it - the bytes of a `bytes` default - is written with escapes, so the file stays text.
    /// </summary>
    static string literal(string text, out bool cut)
    {
        text = text.Replace("\r\n", "\n");
        if( cut = MAX_CONSTANT < text.Length )
        {
            var keep = MAX_CONSTANT - 1;
            if( char.IsHighSurrogate(text[keep - 1]) ) keep--; // never split a surrogate pair
            text = text[..keep].TrimEnd() + "…";
        }

        if( !text.Any(c => char.IsControl(c) && c != '\n') )
            return "@\"" + text.Replace("\"", "\"\"") + "\"";

        var sb = new StringBuilder("\"");
        foreach( var c in text )
            sb.Append(c switch
                      {
                          '\\' => "\\\\",
                          '"'  => "\\\"",
                          '\n' => "\\n",
                          '\t' => "\\t",
                          '\0' => "\\0",
                          _    => char.IsControl(c) ? $"\\u{(int)c:X4}" : c.ToString()
                      });
        return sb.Append('"').ToString();
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  One conversion: a .proto file and what it imports → one description
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>A .proto file that is read without an error and still gives no description.</summary>
    sealed class NothingToSend(string why) : Exception(why);

    /// <summary>What a field is typed with in the description.</summary>
    readonly record struct TypeRef(string text, string attribute = "", bool nullable = false, string note = "");

    /// <summary>A segment of a package other than the file's own, as it is written out: a struct that only holds types.</summary>
    sealed class Container(string segment, Container? parent){
        public readonly string       segment = segment;
        public readonly Container?   parent  = parent;
        public readonly List<object> items   = []; // types and the containers below, in the order they are written
    }

    /// <summary>A oneof as it is written out: a pack of optional alternatives, nested in its message.</summary>
    sealed class Union(ProtoMessage owner, ProtoOneof oneof){
        public readonly ProtoMessage owner = owner;
        public readonly ProtoOneof   oneof = oneof;
    }

    sealed class Conversion(Library library, ProtoFile top, string project){
        public readonly List<string> warnings = [];

        // Every file the conversion sees: the file itself first, then what it imports, as deep as it goes.
        readonly List<ProtoFile> files = [];

        // Every message and enum of these files by the name protobuf knows it by: `pkg.Outer.Inner`.
        readonly Dictionary<string, ProtoNode> symbols = new();

        // The top-level types that are written out, in the order they are written: those of the file, then those of
        // the imported files that something written out is typed with.
        readonly List<ProtoNode> emitted = [];
        readonly HashSet<ProtoNode> wanted = new(ReferenceEqualityComparer.Instance);

        // The fields `extend` blocks add, by the message they are added to.
        readonly Dictionary<ProtoMessage, List<(ProtoField field, ProtoExtend block)>> extensions = new(ReferenceEqualityComparer.Instance);

        readonly Dictionary<ProtoOneof, Union> unions = new(ReferenceEqualityComparer.Instance);

        // Names. A type: a message, an enum, the pack of a oneof, a service, a method. A member: a field, an enum value.
        readonly Dictionary<object, string> type_name   = new(ReferenceEqualityComparer.Instance);
        readonly Dictionary<object, string> member_name = new(ReferenceEqualityComparer.Instance);

        // What the converter declares itself; each yields its name to a type of the document.
        string NoArg = "NoArg", EndOfStream = "EndOfStream", DurationMs = "DurationMs", Client = "Client", Server = "Server",
               Connection = "ClientServerConnection", Exchange = "Exchange";

        bool uses_NoArg, uses_EndOfStream, uses_DurationMs, uses_wide, uses_bytes;

        readonly Dictionary<string, string> attributes = new(); // the custom attributes used, by name: their declarations

        // The packs that travel on their own, with the services that carry them: the Packs Inventory.
        readonly SortedDictionary<string, SortedSet<string>> inventory = new(StringComparer.Ordinal);

        // ───────────────────────────────────────────────────────── load

        void load(ProtoFile f)
        {
            if( files.Contains(f) ) return;
            files.Add(f);

            foreach( var m in f.messages ) register(m);
            foreach( var e in f.enums ) symbols.TryAdd(e.full_name, e);

            foreach( var name in f.imports )
                if( library.import(f, name) is { } imported ) load(imported);
                else if( !name.StartsWith("google/protobuf/") ) warnings.Add($"import \"{name}\" of {f.name} was not found: what it declares is not known here");
        }

        void register(ProtoMessage m)
        {
            symbols.TryAdd(m.full_name, m);
            foreach( var n in m.messages ) register(n);
            foreach( var e in m.enums ) symbols.TryAdd(e.full_name, e);
        }

        /// <summary>
        /// The message or enum a name means where it is written, the way protobuf resolves it: in the scope it
        /// stands in, then in the scopes around that one, up to the root. A leading dot starts at the root.
        /// </summary>
        ProtoNode? resolve(string name, string scope)
        {
            if( name.StartsWith('.') ) return symbols.GetValueOrDefault(name[1..]);

            while( true )
            {
                if( symbols.TryGetValue(scope == "" ? name : scope + "." + name, out var found) ) return found;
                if( scope == "" ) return null;
                var dot = scope.LastIndexOf('.');
                scope = dot < 0 ? "" : scope[..dot];
            }
        }

        static bool is_scalar(string type) => type is "double" or "float" or "int32" or "int64" or "uint32" or "uint64" or "sint32" or "sint64" or
                                                  "fixed32" or "fixed64" or "sfixed32" or "sfixed64" or "bool" or "string" or "bytes";

        static bool is_well_known(ProtoNode n) => n is ProtoMessage { parent: null, file.package: "google.protobuf" } m &&
                                                  m.name is "Timestamp" or "Duration" or "Empty" or "Any" or "FieldMask" or
                                                      "DoubleValue" or "FloatValue" or "Int64Value" or "UInt64Value" or "Int32Value" or "UInt32Value" or
                                                      "BoolValue" or "StringValue" or "BytesValue";

        static bool is_empty_message(ProtoNode n) => n is ProtoMessage { parent: null, file.package: "google.protobuf", name: "Empty" };

        /// <summary>An extension of an options message declares an option of the language, not data: nothing to carry.</summary>
        static bool declares_option(ProtoMessage target) => target.file.package == "google.protobuf" && target.name.EndsWith("Options");

        static ProtoNode top_level(ProtoNode n) => n switch
                                                   {
                                                       ProtoMessage { parent: { } p } => top_level(p),
                                                       ProtoEnum { parent: { } p }    => top_level(p),
                                                       _                              => n
                                                   };

        static ProtoFile file_of(ProtoNode n) => n is ProtoMessage m ? m.file : ((ProtoEnum)n).file;

        /// <summary>What a type is declared in: a message, the container of its package, or the project (null).</summary>
        object? parent_of(object n) => n switch
                                       {
                                           ProtoMessage m => (object?)m.parent ?? container_of.GetValueOrDefault(m),
                                           ProtoEnum e    => (object?)e.parent ?? container_of.GetValueOrDefault(e),
                                           Union u        => u.owner,
                                           Container c    => c.parent,
                                           _              => null
                                       };

        // ───────────────────────────────────────────────────────── packages

        // The package of the file is the namespace of the description: what the file declares stands in the project.
        // A type of another package stands in containers named after that package, `google.rpc.Status`: the
        // hierarchy of the names is the hierarchy of the declarations, not a text beside them.
        readonly Dictionary<string, Container>    containers   = new();
        readonly Dictionary<ProtoNode, Container> container_of = new(ReferenceEqualityComparer.Instance);

        // What the project declares, in the order it is written: types and the containers of other packages.
        readonly List<object> project_items = [];

        Container container(string package)
        {
            if( containers.TryGetValue(package, out var c) ) return c;

            var dot    = package.LastIndexOf('.');
            var parent = dot < 0 ? null : container(package[..dot]);
            c = containers[package] = new Container(package[(dot + 1)..], parent);
            (parent?.items ?? project_items).Add(c);
            return c;
        }

        void place()
        {
            foreach( var n in emitted )
            {
                var package = file_of(n).package;
                if( package == top.package || package == "" ) project_items.Add(n);
                else
                {
                    var c = container(package);
                    container_of[n] = c;
                    c.items.Add(n);
                }
            }
        }

        /// <summary>C# keywords: a segment of a package that is one cannot be a segment of a namespace as it is.</summary>
        static readonly HashSet<string> CSHARP_KEYWORDS =
        [
            "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class", "const", "continue",
            "decimal", "default", "delegate", "do", "double", "else", "enum", "event", "explicit", "extern", "false", "finally",
            "fixed", "float", "for", "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock",
            "long", "namespace", "new", "null", "object", "operator", "out", "override", "params", "private", "protected",
            "public", "readonly", "ref", "return", "sbyte", "sealed", "short", "sizeof", "stackalloc", "static", "string",
            "struct", "switch", "this", "throw", "true", "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort",
            "using", "virtual", "void", "volatile", "while",
        ];

        /// <summary>The namespace of the description: the package of the file, segment by segment.</summary>
        string description_namespace() => top.package == "" ?
                                              "org.protobuf" :
                                              string.Join('.', top.package.Split('.').Select(s => CSHARP_KEYWORDS.Contains(s) ? char.ToUpperInvariant(s[0]) + s[1..] : s));

        /// <summary>The fields a message has: its own, those of its oneofs, those added by `extend` blocks.</summary>
        IEnumerable<ProtoField> all_fields(ProtoMessage m) => m.fields.Concat(m.oneofs.SelectMany(o => o.fields))
                                                               .Concat(extensions.TryGetValue(m, out var x) ? x.Select(e => e.field) : []);

        /// <summary>A message that carries nothing: as the type of a field its presence is all it says.</summary>
        bool says_nothing(ProtoMessage m) => !all_fields(m).Any();

        // ───────────────────────────────────────────────────────── what is written out

        readonly Queue<ProtoMessage> to_follow = new();

        /// <summary>
        /// A type is written out with the top-level message it is nested in. What it is typed with is followed
        /// later, by <see cref="follow_all"/>: so the types of the file come first, in the order of the file.
        /// </summary>
        void want(ProtoNode? n)
        {
            if( n == null || is_well_known(n) ) return;
            n = top_level(n);
            if( !wanted.Add(n) ) return;
            emitted.Add(n);
            if( n is ProtoMessage m ) to_follow.Enqueue(m);
        }

        void follow_all()
        {
            while( to_follow.TryDequeue(out var m) ) follow(m);
        }

        /// <summary>Whatever the fields of a message, and of the messages nested in it, are typed with is written out too.</summary>
        void follow(ProtoMessage m)
        {
            foreach( var f in all_fields(m) )
                if( !is_scalar(f.type) && f.group == null )
                    want(resolve(f.type, scope_of(f, m)));

            foreach( var n in m.messages ) follow(n);
        }

        /// <summary>The scope the type of a field is resolved in: the message it is written in.</summary>
        string scope_of(ProtoField f, ProtoMessage m)
        {
            // A field an `extend` block adds is written in the scope of that block, not in the message it lands in.
            if( extensions.TryGetValue(m, out var added) )
                foreach( var (field, block) in added )
                    if( field == f )
                        return block.scope?.full_name ?? block.file.package;

            return m.full_name;
        }

        void collect_extensions()
        {
            IEnumerable<ProtoExtend> blocks(ProtoMessage m) => m.extends.Concat(m.messages.SelectMany(blocks));

            foreach( var f in files )
                foreach( var block in f.extends.Concat(f.messages.SelectMany(blocks)) )
                {
                    if( resolve(block.target, block.scope?.full_name ?? f.package) is not ProtoMessage target )
                    {
                        if( f == top ) warnings.Add($"line {block.line}: `extend {block.target}` - the message is not declared in the files that were found");
                        continue;
                    }

                    if( declares_option(target) ) continue;

                    if( !extensions.TryGetValue(target, out var list) ) extensions[target] = list = [];
                    foreach( var field in block.fields ) list.Add((field, block));
                }
        }

        // ───────────────────────────────────────────────────────── names

        string name_of(object type) => type_name[type];

        /// <summary>What a type is nested in, outermost first - containers of its package, messages - and the type itself.</summary>
        List<object> chain(object type)
        {
            var list = new List<object>();
            for( object? n = type; n != null; n = parent_of(n) ) list.Insert(0, n);
            return list;
        }

        /// <summary>
        /// How a type is named where it is used: from the project by its full path, from inside the message it
        /// is nested in - or from a sibling - by the part of the path that is not shared.
        /// </summary>
        string reference(object target, object? from)
        {
            var path   = chain(target);
            var common = 0;
            if( from != null )
            {
                var mine = chain(from);
                while( common < path.Count - 1 && common < mine.Count && ReferenceEquals(path[common], mine[common]) ) common++;
            }

            return string.Join('.', path.Skip(common).Select(name_of));
        }

        void settle_names()
        {
            var enclosing = new HashSet<string>(IMPORTED_NAMES);
            enclosing.UnionWith(NOT_A_TYPE_NAME);
            enclosing.Add(project);

            // What stands in the project: the types of the file's package, in the order of the file, and the
            // containers of the other packages.
            var siblings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach( var item in project_items )
            {
                type_name[item] = recase(brush(item is Container c ? c.segment : ((ProtoNode)item).name), s => siblings.Contains(s) || enclosing.Contains(s));
                siblings.Add(type_name[item]);
            }

            // Then what the converter declares itself.
            string own(string name, params string[] others)
            {
                foreach( var candidate in others.Prepend(name) )
                    if( !siblings.Contains(candidate) && !enclosing.Contains(candidate) )
                    {
                        siblings.Add(candidate);
                        return candidate;
                    }

                name = recase(name, s => siblings.Contains(s) || enclosing.Contains(s));
                siblings.Add(name);
                return name;
            }

            NoArg       = own("NoArg", "NoArgs");
            EndOfStream = own("EndOfStream", "StreamEnd");
            DurationMs  = own("DurationMs", "ProtoDuration");
            Client      = own("Client", "ClientHost");
            Server      = own("Server", "ServerHost");
            Connection  = own("ClientServerConnection", "ClientServerLink");

            enclosing.UnionWith(siblings);

            foreach( var item in project_items ) settle_item(item, enclosing);

            // The services stand in the connection, the methods in their service: each is a type or may become one.
            var services = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach( var s in top.services )
            {
                type_name[s] = recase(brush(s.name), n => services.Contains(n) || enclosing.Contains(n));
                services.Add(type_name[s]);
            }

            Exchange = recase(Exchange, n => services.Contains(n) || enclosing.Contains(n));

            // A method cannot be called like its service - `service Watch { rpc Watch … }` - nor like another type in
            // sight. It is the deeper of the two, so it is the one that changes, by its case.
            var in_sight = new HashSet<string>(services);
            foreach( var s in top.services )
            {
                var methods = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach( var rpc in s.rpcs )
                {
                    type_name[rpc] = recase(brush(rpc.name), n => methods.Contains(n) || enclosing.Contains(n) || in_sight.Contains(n));
                    methods.Add(type_name[rpc]);
                }

                one_way_actor[s] = recase("OneWay", n => methods.Contains(n) || enclosing.Contains(n) || in_sight.Contains(n));
            }
        }

        // A map keyed by a boolean is written as two fields: their names, by the field of the .proto.
        readonly Dictionary<ProtoField, (string if_false, string if_true)> bool_map = new(ReferenceEqualityComparer.Instance);

        // The actor that holds the one-way calls of a service, by the service.
        readonly Dictionary<ProtoService, string> one_way_actor = new(ReferenceEqualityComparer.Instance);

        void settle_item(object item, HashSet<string> enclosing)
        {
            switch( item )
            {
                case ProtoMessage m:
                    settle(m, enclosing);
                    break;
                case ProtoEnum e:
                    settle(e);
                    break;
                case Container c:
                    // A container holds types and the containers of the packages below its own.
                    var mine     = type_name[c];
                    var siblings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach( var inner in c.items )
                    {
                        type_name[inner] = recase(brush(inner is Container sub ? sub.segment : ((ProtoNode)inner).name),
                                                  s => s == mine || siblings.Contains(s) || enclosing.Contains(s));
                        siblings.Add(type_name[inner]);
                    }

                    var added = siblings.Where(enclosing.Add).ToList();
                    foreach( var inner in c.items ) settle_item(inner, enclosing);
                    enclosing.ExceptWith(added);
                    break;
            }
        }

        /// <summary>
        /// Settles the names inside a message: of the types nested in it, then of its fields.
        /// <paramref name="enclosing"/>: the type names visible from outside it. A nested type that takes one of them
        /// would change what the name means inside, so the nested one - the deeper one - changes.
        /// </summary>
        void settle(ProtoMessage m, HashSet<string> enclosing)
        {
            var mine     = type_name[m];
            var fields   = m.fields.Select(f => brush(f.name)).Concat(m.oneofs.Select(o => brush(o.name))).ToHashSet();
            var siblings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            bool taken(string s) => s == mine || siblings.Contains(s) || enclosing.Contains(s) || fields.Contains(s);

            // The messages and enums of the document first: their names are its authors' own.
            var nested = m.body.Where(item => item is ProtoMessage or ProtoEnum).ToList();
            foreach( var item in nested )
            {
                type_name[item] = recase(brush(((ProtoNode)item).name), taken);
                siblings.Add(type_name[item]);
            }

            // Then the packs of the oneofs, which the converter makes. One is named after its oneof, `kind` → `Kind`,
            // and says what it is when that name is taken.
            foreach( var o in m.oneofs )
            {
                var union = unions[o] = new Union(m, o);
                var name  = brush(o.name);
                name             = char.ToUpperInvariant(name[0]) + name[1..];
                type_name[union] = taken(name) && !taken(name + "Oneof") ? name + "Oneof" : recase(name, taken);
                siblings.Add(type_name[union]);
            }

            // Fields: none may be called like the pack, like a type nested in it or like another field. A field and
            // a type that differ by case are two names: `Kind kind;` is how a field of a nested type usually reads.
            var types = new HashSet<string>(siblings);
            var seen  = new HashSet<string>();
            string free(string name)
            {
                name = recase(brush(name), s => s == mine || seen.Contains(s) || types.Contains(s));
                seen.Add(name);
                return name;
            }

            void member(object field, string name)
            {
                if( field is ProtoField { map_key: "bool" } two ) bool_map[two] = (free(name + "_false"), free(name + "_true"));
                else member_name[field] = free(name);
            }

            foreach( var item in m.body )
                switch( item )
                {
                    case ProtoField f: member(f, f.name); break;
                    case ProtoOneof o: member(o, o.name); break;
                }

            if( extensions.TryGetValue(m, out var added) )
                foreach( var (f, _) in added )
                    member(f, f.name);

            foreach( var o in m.oneofs )
            {
                var own  = type_name[unions[o]];
                var used = new HashSet<string>();
                foreach( var f in o.fields )
                {
                    member_name[f] = recase(brush(f.name), s => s == own || used.Contains(s));
                    used.Add(member_name[f]);
                }
            }

            var added_names = siblings.Where(enclosing.Add).ToList();
            foreach( var n in nested )
                if( n is ProtoMessage inner ) settle(inner, enclosing);
                else settle((ProtoEnum)n);
            enclosing.ExceptWith(added_names);
        }

        void settle(ProtoEnum e)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach( var v in e.values )
            {
                member_name[v] = recase(brush(v.name), seen.Contains);
                seen.Add(member_name[v]);
            }
        }

        // ───────────────────────────────────────────────────────── types

        /// <summary>An enum with fewer than two values is no enum to AdHoc: it is written as constants, a field of it is a number.</summary>
        static bool is_constants(ProtoEnum e) => e.values.Count() < 2;

        TypeRef scalar(string type)
        {
            switch( type )
            {
                // The author chose a varint: small values are the common ones. [X] spends the saving on both signs.
                case "int32" or "sint32": return new TypeRef("int", "X");
                case "int64" or "sint64":
                    uses_wide = true;
                    return new TypeRef("long", "X");
                case "uint32": return new TypeRef("uint", "A");
                case "uint64":
                    uses_wide = true;
                    return new TypeRef("ulong", "A");
                // A fixed width is a statement too: the values are spread over the range.
                case "fixed32":  return new TypeRef("uint");
                case "sfixed32": return new TypeRef("int");
                case "fixed64":
                    uses_wide = true;
                    return new TypeRef("ulong");
                case "sfixed64":
                    uses_wide = true;
                    return new TypeRef("long");
                case "bytes":
                    uses_bytes = true;
                    return new TypeRef("Binary[,,]");
                default: return new TypeRef(type); // double float bool string
            }
        }

        /// <summary>The type of one value of a field: of the field itself, of an item of a repeated one, of a key or a value of a map.</summary>
        TypeRef value_type(string type, string scope, object? from, int line)
        {
            if( is_scalar(type) ) return scalar(type);

            var target = resolve(type, scope);
            switch( target )
            {
                case null:
                    warnings.Add($"line {line}: the type `{type}` is not declared in the files that were found: carried as raw bytes");
                    uses_bytes = true;
                    return new TypeRef("Binary[,,]", note: $"`{type}` is not declared in the files that were found: raw bytes");

                case ProtoEnum e when is_constants(e):
                    return new TypeRef("int", "X", note: $"values: the constants of {reference(e, from)}");

                case ProtoEnum e: return new TypeRef(reference(e, from));

                case ProtoMessage m when is_well_known(m):
                    switch( m.name )
                    {
                        case "Timestamp": return new TypeRef("DateTime");
                        case "Duration":
                            uses_DurationMs = true;
                            return new TypeRef(DurationMs);
                        case "Empty": return new TypeRef("bool", note: "google.protobuf.Empty: being there is all it says");
                        case "Any":
                            uses_bytes = true;
                            return new TypeRef("Binary[,,]", note: "google.protobuf.Any: a serialized message and the URL of its type");
                        case "FieldMask": return new TypeRef("string[,,]", note: "google.protobuf.FieldMask: the paths of the fields");
                        // A wrapper exists to make a scalar optional, and AdHoc says that with `?`.
                        case "DoubleValue": return new TypeRef("double", nullable: true);
                        case "FloatValue":  return new TypeRef("float", nullable: true);
                        case "BoolValue":   return new TypeRef("bool", nullable: true);
                        case "StringValue": return new TypeRef("string", nullable: true);
                        case "BytesValue":
                            uses_bytes = true;
                            return new TypeRef("Binary[,,]", nullable: true);
                        case "Int32Value": return new TypeRef("int", "X", true);
                        case "Int64Value":
                            uses_wide = true;
                            return new TypeRef("long", "X", true);
                        case "UInt32Value": return new TypeRef("uint", "A", true);
                        default:
                            uses_wide = true;
                            return new TypeRef("ulong", "A", true);
                    }

                case ProtoMessage m when says_nothing(m):
                    return new TypeRef("bool", note: $"{reference(m, from)} has no fields: being there is all it says");

                default: return new TypeRef(reference(target, from));
            }
        }

        // ───────────────────────────────────────────────────────── attributes

        /// <summary>A custom attribute: declared once at the end of the project, applied here.</summary>
        string attribute(string name, string declaration, params string[] arguments)
        {
            attributes.TryAdd(name, declaration);
            if( arguments.Length == 0 ) return $"[{name}]\n";

            var cut  = false;
            var args = arguments.Select(a =>
                                        {
                                            if( a.Length < 2 || a[0] != '"' ) return a;
                                            var text = literal(a[1..^1], out var c);
                                            cut |= c;
                                            return text;
                                        }).ToList();
            return $"{(cut ? CUT_NOTE : "")}[{name}({string.Join(", ", args)})]\n";
        }

        const string MULTIPLE = "[AttributeUsage(AttributeTargets.All, AllowMultiple = true)]\n";

        /// <summary>What the options of a declaration say that a description can keep: `deprecated`, and the options the authors declared themselves.</summary>
        string attributes_of(ProtoNode n)
        {
            var sb = new StringBuilder();
            if( n.deprecated ) sb.Append(attribute("Obsolete", "public class ObsoleteAttribute : Attribute { }"));

            foreach( var o in n.options.Where(x => x.is_custom) )
                sb.Append(attribute("Option", MULTIPLE + "public class OptionAttribute : Attribute { public OptionAttribute(string Name, string Value) { } }",
                                    $"\"{o.name}\"", $"\"{o.value}\""));
            return sb.ToString();
        }

        /// <summary>The default a proto2 field declares: metadata, AdHoc sends what the field holds.</summary>
        string default_of(ProtoField f)
        {
            var o = f.options.LastOrDefault(x => x.name == "default");
            if( o == null ) return "";

            const string DECLARATION = "public class DefaultAttribute : Attribute { public DefaultAttribute(double value) { } public DefaultAttribute(long value) { } " +
                                       "public DefaultAttribute(ulong value) { } public DefaultAttribute(string value) { } public DefaultAttribute(bool value) { } }";

            var v = o.value;
            if( o.is_string ) return attribute("Default", DECLARATION, $"\"{v}\"");
            if( v is "true" or "false" ) return attribute("Default", DECLARATION, v);

            if( v.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || v.StartsWith("-0x", StringComparison.OrdinalIgnoreCase) ) return attribute("Default", DECLARATION, v);
            if( long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out _) || ulong.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out _) )
                return attribute("Default", DECLARATION, v);
            if( double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && !double.IsInfinity(d) && !double.IsNaN(d) && v.Any(char.IsDigit) && !v.Contains("inf") && !v.Contains("nan") )
                return attribute("Default", DECLARATION, v.Contains('.') || v.Contains('e') || v.Contains('E') ? v : v + ".0");

            return attribute("Default", DECLARATION, $"\"{v}\""); // a value of an enum by its name, inf, nan
        }

        // ───────────────────────────────────────────────────────── write: types

        void write(ProtoNode n, StringBuilder o)
        {
            if( n is ProtoMessage m ) write(m, o);
            else write((ProtoEnum)n, o);
        }

        void write(ProtoEnum e, StringBuilder o)
        {
            if( is_constants(e) )
            {
                // AdHoc refuses an enum with fewer than two values: a type with one value tells the receiver nothing.
                o.Append($"\n// an enum of the .proto with {(e.values.Any() ? "one value" : "no values")}: written as its constants, a field of it is a number\n");
                o.Append(doc_comment(e.doc));
                o.Append($"public struct {name_of(e)} {{\n");
                foreach( var v in e.values ) o.Append(doc_comment(v.doc)).Append($"public const int {member_name[v]} = {v.number};\n");
                o.Append("}\n\n");
                return;
            }

            o.Append('\n').Append(doc_comment(e.doc)).Append(attributes_of(e));
            o.Append($"public enum {name_of(e)} {{\n");
            foreach( var item in e.body )
                switch( item )
                {
                    case ProtoEnumValue v:
                        var said = v.doc;
                        if( v.deprecated ) said = (said == "" ? "" : said + "\n") + "deprecated";
                        foreach( var opt in v.options.Where(x => x.is_custom) ) said = (said == "" ? "" : said + "\n") + $"option {opt.name} = {one_line(opt.value)}";
                        o.Append(doc_comment(said));
                        o.Append($"{member_name[v]} = {v.number},{(member_name[v] == v.name ? "" : $" // {v.name}")}\n");
                        break;
                    case ProtoNote note:
                        o.Append($"// {note.text}\n");
                        break;
                }

            o.Append("}\n\n"); // a type stands apart from what follows it
        }

        void write(ProtoMessage m, StringBuilder o)
        {
            var said = m.doc;
            if( m.option("message_set_wire_format") == "true" )
                said = (said == "" ? "" : said + "\n") + "A MessageSet in protobuf: a container of extensions, written here as the fields they add.";

            o.Append('\n').Append(doc_comment(said)).Append(attributes_of(m));
            o.Append($"public class {name_of(m)} {{\n");

            foreach( var item in m.body )
                switch( item )
                {
                    case ProtoField f:
                        write(f, m, m, o);
                        break;

                    case ProtoOneof oneof:
                        // What a oneof says is said by a pack of optional alternatives: the receiver tests which one is set.
                        var union = unions[oneof];
                        o.Append('\n').Append(doc_comment((oneof.doc == "" ? "" : oneof.doc + "\n") + "A oneof in protobuf: one of the alternatives is set."));
                        o.Append(attributes_of(oneof));
                        o.Append($"public class {name_of(union)} {{\n");
                        foreach( var f in oneof.fields ) write(f, m, union, o);
                        o.Append("}\n\n");
                        o.Append($"{name_of(union)} {member_name[oneof]};\n");
                        break;

                    case ProtoMessage or ProtoEnum:
                        write((ProtoNode)item, o);
                        break;

                    case ProtoNote note:
                        o.Append($"// {note.text}\n");
                        break;
                }

            if( extensions.TryGetValue(m, out var added) )
                foreach( var group in added.GroupBy(x => x.block) )
                {
                    var where = group.Key.scope != null   ? $" in {group.Key.scope.full_name}" :
                                group.Key.file != m.file ? $" in {group.Key.file.name}" :
                                                           "";
                    o.Append($"\n// extension fields: `extend {group.Key.target}`{where}\n");
                    foreach( var (f, _) in group ) write(f, m, m, o);
                }

            o.Append("}\n\n");
        }

        /// <summary>
        /// A field of <paramref name="m"/>, written in <paramref name="pack"/>: in the message itself, or in the
        /// pack of the oneof it is an alternative of.
        /// </summary>
        void write(ProtoField f, ProtoMessage m, object pack, StringBuilder o)
        {
            var scope = scope_of(f, m);
            var notes = new List<string>(); // what the type alone does not say: written after the `;` of the field
            string trail() => notes.Count == 0 ? "" : " // " + string.Join("; ", notes);
            var attrs = new StringBuilder();
            string type;

            if( f.map_key == "bool" )
            {
                // A map keyed by a boolean holds two values at most: two fields say it plainly.
                var v = value_type(f.type, scope, pack, f.line);
                if( v.note != "" ) notes.Add(v.note);
                var (if_false, if_true) = bool_map[f];
                o.Append($"\n// map<bool, {f.type}> {f.name}: a field for each of the two keys\n");
                o.Append(doc_comment(f.doc)).Append(attributes_of(f));
                o.Append($"{(v.attribute == "" ? "" : $"[{v.attribute}] ")}{v.text}? {if_false};{trail()}\n");
                o.Append($"{(v.attribute == "" ? "" : $"[{v.attribute}] ")}{v.text}? {if_true};{trail()}\n\n");
                return;
            }

            if( f.map_key != null )
            {
                var k = scalar(f.map_key);
                var v = value_type(f.type, scope, pack, f.line);
                if( k.attribute != "" ) attrs.Append($"[Key: {k.attribute}] ");
                if( v.attribute != "" ) attrs.Append($"[Val: {v.attribute}] ");
                if( v.note != "" ) notes.Add(v.note);
                type = $"Map<{k.text}, {v.text}>";
            }
            else
            {
                var t = f.group != null ? new TypeRef(reference(f.group, pack)) : value_type(f.type, scope, pack, f.line);
                if( t.attribute != "" ) attrs.Append($"[{t.attribute}] ");
                if( t.note != "" ) notes.Add(t.note);

                // Optional is what the field says of itself: the label, being an alternative of a oneof, a wrapper type.
                // Without a label an edition gives a field presence, proto3 does not.
                var optional = f.label == "optional" || f.oneof != null || t.nullable || f.label == "" && has_presence(f, m);
                type = f.repeated ? t.text + "[,,]" :
                       optional   ? t.text + "?" :
                                    t.text;
            }

            var above = doc_comment(f.doc) + attributes_of(f) + default_of(f);
            if( above != "" ) o.Append('\n');
            o.Append(above).Append($"{attrs}{type} {member_name[f]};{trail()}\n");
        }

        /// <summary>An edition gives a field without a label explicit presence unless a feature says otherwise.</summary>
        bool has_presence(ProtoField f, ProtoMessage m)
        {
            if( m.file.syntax != "editions" || !is_scalar(f.type) && resolve(f.type, m.full_name) is ProtoMessage ) return false;
            var presence = f.option("features.field_presence") ?? m.file.option("features.field_presence") ?? "EXPLICIT";
            return presence == "EXPLICIT";
        }

        // ───────────────────────────────────────────────────────── write: services

        /// <summary>The pack a method sends or answers with; an empty message of protobuf is AdHoc's empty pack.</summary>
        string pack(string type, ProtoService s, ProtoRpc rpc)
        {
            var target = resolve(type, s.file.package);
            string name;
            if( target is ProtoMessage m && is_empty_message(m) )
            {
                uses_NoArg = true;
                name       = NoArg;
            }
            else if( target is ProtoMessage found ) name = reference(found, null);
            else
            {
                warnings.Add($"line {rpc.line}: the type `{type}` of `{s.name}.{rpc.name}` is not declared in the files that were found: {NoArg} stands for it");
                uses_NoArg = true;
                return NoArg;
            }

            if( !inventory.TryGetValue(name, out var tags) ) inventory[name] = tags = new SortedSet<string>(StringComparer.Ordinal);
            tags.Add(type_name[s]);
            return name;
        }

        bool is_empty(string type, ProtoService s) => resolve(type, s.file.package) is ProtoMessage m && is_empty_message(m);

        /// <summary>
        /// gRPC knows a method as `/package.Service/Method`, and the description says the same by where the method
        /// is declared: the namespace, the interface of the service, the method. A name that had to change says
        /// what it is called in the .proto, so the path can still be told.
        /// </summary>
        string renamed(ProtoNode n) => type_name[n] == n.name ? "" : $"\n`{n.name}` in the .proto";

        void write(ProtoService s, StringBuilder o)
        {
            var methods = new StringBuilder();
            var one_way = new StringBuilder(); // the calls nothing comes back from: the states of one actor

            foreach( var rpc in s.rpcs )
            {
                var name     = type_name[rpc];
                var request  = pack(rpc.request, s, rpc);
                var response = !rpc.request_stream && !rpc.response_stream && is_empty(rpc.response, s) ? "" : pack(rpc.response, s, rpc); // a one-way call has none
                var head     = "\n" + doc_comment(rpc.doc + renamed(rpc)) + attributes_of(rpc);

                if( !rpc.request_stream && !rpc.response_stream )
                {
                    // Nothing comes back: a one-way call. A state stands for the call, its branch sends the pack.
                    if( response == "" ) one_way.Append(head).Append($"[l____________<{request}>]\nstruct {name} {{ }}\n");
                    else methods.Append(head).Append($"(L____________, {response}) {name}({(is_empty(rpc.request, s) ? $"{NoArg} _" : $"{request} req")});\n");

                    continue;
                }

                methods.Append(head);

                // A stream is not one pack but many: the items go by a non-transitional branch while the actor
                // stays in its state, and an empty pack tells that there are no more.
                uses_EndOfStream = true;
                methods.Append($"public interface {name} : Actor {{\n");
                methods.Append("int MaxActiveInstances => UNLIMITED;\n\n");

                if( rpc.request_stream && rpc.response_stream )
                {
                    methods.Append($"[l____________<{request}>]\n");
                    methods.Append($"[____________r<{response}>]\n");
                    methods.Append($"[L____________<Drain_{name}, {EndOfStream}>]\n");
                    methods.Append($"struct Call_{name} {{ }}\n\n");
                    methods.Append($"[____________r<{response}>]\n");
                    methods.Append($"[____________R<End, {EndOfStream}>]\n");
                    methods.Append($"struct Drain_{name} {{ }}\n");
                }
                else if( rpc.request_stream )
                {
                    methods.Append($"[l____________<{request}>]\n");
                    methods.Append($"[L____________<Return_{name}, {EndOfStream}>]\n");
                    methods.Append($"struct Call_{name} {{ }}\n\n");
                    methods.Append($"[____________R<End, {response}>]\n");
                    methods.Append($"struct Return_{name} {{ }}\n");
                }
                else
                {
                    methods.Append($"[L____________<Return_{name}, {request}>]\n");
                    methods.Append($"struct Call_{name} {{ }}\n\n");
                    methods.Append($"[____________r<{response}>]\n");
                    methods.Append($"[____________R<End, {EndOfStream}>]\n");
                    methods.Append($"struct Return_{name} {{ }}\n");
                }

                methods.Append("}\n");
            }

            var said = s.doc + renamed(s);
            if( s.deprecated ) said += "\ndeprecated";
            foreach( var opt in s.options.Where(x => x.is_custom) ) said += $"\noption {opt.name} = {one_line(opt.value)}";
            o.Append('\n').Append(doc_comment(said));
            o.Append($"public interface {type_name[s]} {{").Append(methods);

            // An RPC method cannot stand in an actor, so the one-way calls get an actor of their own. It has no
            // transitions: no instance of it is ever made, its states are always there.
            if( 0 < one_way.Length )
                o.Append("\n/// <summary>The calls of the service that nothing comes back from: a state for each, its branch sends the pack.</summary>\n")
                 .Append($"public interface {one_way_actor[s]} : Actor {{").Append(one_way).Append("}\n");

            o.Append("}\n");
        }

        // ───────────────────────────────────────────────────────── run

        public string run()
        {
            load(top);
            collect_extensions();

            // What the file declares is written out, and what that is typed with.
            foreach( var n in top.messages.Cast<ProtoNode>().Concat(top.enums).OrderBy(x => x.line) ) want(n);

            foreach( var block in top.extends.Concat(top.messages.SelectMany(all_extends)) )
                if( resolve(block.target, block.scope?.full_name ?? top.package) is ProtoMessage target && !declares_option(target) )
                    want(target);

            foreach( var s in top.services )
                foreach( var rpc in s.rpcs )
                {
                    want(resolve(rpc.request, top.package));
                    want(resolve(rpc.response, top.package));
                }

            follow_all();

            // A description is a connection and what travels over it. A file of enums or of option declarations
            // alone has nothing to send: what it declares is written out with the files that use it.
            if( top.services.Count == 0 && !emitted.OfType<ProtoMessage>().Any(m => m.file == top) )
                throw new NothingToSend(top.enums.Count == 0 ? "it declares no message, enum or service" : "it declares enums only: they are written out with the files that use them");

            place();
            settle_names();

            // ── The connection first: writing it finds out which of the converter's own packs are used.
            var services = new StringBuilder();
            foreach( var s in top.services ) write(s, services);

            // The messages of the file that no method carries and no field is typed with are the ones that travel
            // on their own; without services that is all a .proto says about what travels.
            var typed    = new HashSet<ProtoNode>(ReferenceEqualityComparer.Instance);
            var carried  = new HashSet<ProtoNode>(ReferenceEqualityComparer.Instance);
            foreach( var m in emitted.OfType<ProtoMessage>() ) used_types(m, typed);
            foreach( var s in top.services )
                foreach( var rpc in s.rpcs )
                {
                    if( resolve(rpc.request, top.package) is { } q ) carried.Add(q);
                    if( resolve(rpc.response, top.package) is { } a ) carried.Add(a);
                }

            var own      = emitted.OfType<ProtoMessage>().Where(m => m.file == top && !carried.Contains(m)).ToList();
            var exchange = own.Where(m => !typed.Contains(m)).ToList();
            if( exchange.Count == 0 && top.services.Count == 0 ) exchange = own; // every message is a part of another: a ring

            foreach( var m in exchange ) inventory.TryAdd(name_of(m), new SortedSet<string>());

            // ── The packs
            var packs = new StringBuilder();
            write_items(project_items, top, packs);

            // ── The file
            var o = new StringBuilder();
            o.Append("// Generated by Protobuf2AdHoc (https://github.com/AdHoc-Protocol)\n");
            o.Append($"// Sources: {top.name}{(top.package == "" ? "" : $", package {top.package}")} ({(top.syntax == "editions" ? "edition " + top.edition : top.syntax)})\n");
            if( Originals.of(top.path) is { } original ) o.Append($"//          {original}\n");
            var imported = emitted.Select(file_of).Where(f => f != top).Distinct().ToList();
            if( 0 < imported.Count )
            {
                o.Append($"// With the types it uses from: {string.Join(", ", imported.Select(f => f.name))}\n");
                foreach( var f in imported )
                    if( Originals.of(f.path) is { } page ) o.Append($"//          {page}\n");
            }
            o.Append("// Re-run the converter instead of editing this file by hand.\n\n");
            o.Append("using System;\nusing org.unirail.Meta;\n\n");
            // The package of the file is the namespace: a package is what a namespace is called in protobuf.
            var ns = description_namespace();
            o.Append($"namespace {ns} {{ // {(top.package == "" ? "replace with your company namespace" : ns == top.package ? "the package of the .proto" : $"the package of the .proto, `{top.package}`")}\n");

            o.Append("""
                     /**
                     Packs Inventory. One line per pack that travels on its own, pre-tagged with the services
                     that carry it - route packs in branches by these tags (KeepDoc). Add your own tags/emojis
                     after the `/>`. On its first run AdHocAgent carries the tags into its numbers tables,
                     numbers the packs and gives a wire id to each pack that becomes directly transmittable.


                     """);
            if( uses_NoArg ) inventory.TryAdd(NoArg, new SortedSet<string>());
            if( uses_EndOfStream ) inventory.TryAdd(EndOfStream, new SortedSet<string>());
            foreach( var (name, tags) in inventory )
                o.Append($"<see cref = '{project}.{name}'/>{(tags.Count == 0 ? "" : " " + string.Join(" | ", tags))}\n");
            o.Append("*/\n");

            foreach( var opt in top.options.Where(x => x.is_custom) )
                o.Append(attribute("Option", MULTIPLE + "public class OptionAttribute : Attribute { public OptionAttribute(string Name, string Value) { } }",
                                   $"\"{opt.name}\"", $"\"{opt.value}\""));
            if( top.option("deprecated") == "true" ) o.Append(attribute("Obsolete", "public class ObsoleteAttribute : Attribute { }"));

            o.Append($"public interface {project} {{\n");

            if( uses_wide )
                o.Append("""
                         // 64-bit integers are `long` / `ulong` here, as the .proto says. In JavaScript a number goes
                         // through a double: if TypeScript hosts matter and the values stay below 2^53, `longJS` /
                         // `ulongJS` are the cheaper types.
                         //

                         """);
            o.Append("""
                     // [X] and [A] are what the .proto itself says: its author chose a varint for these fields, so
                     // small values are the common ones. Where that is not so - an identifier, a hash, a timestamp -
                     // drop the attribute: a varint loses on values that are systematically large.

                     """);
            if( uses_bytes )
                o.Append("""
                         //
                         // `bytes` is `Binary[,,]` here: a value held whole. A field that carries a BLOB - a file, a
                         // serialized object - is better declared as a conduit, `[S(N)] File`, which is never held whole.

                         """);

            o.Append("""

                     // Caps for collections that carry no explicit [D(...)] attribute. Protobuf declares no bounds, so
                     // these permissive defaults replace AdHoc's own limit of 255 items/chars. Put [D(+N)] on a
                     // field whose real bound is known and tighten the rest.
                     enum _DefaultMaxLengthOf { Arrays = 65_535, Maps = 65_535, Sets = 65_535, Strings = 65_535, }

                     """);

            if( uses_NoArg )
                o.Append($$"""

                           /// <summary>The empty request or reply: google.protobuf.Empty.</summary>
                           public class {{NoArg}} { }

                           """);
            if( uses_EndOfStream )
                o.Append($$"""

                           /// <summary>Ends a stream of packs: there are no more of them.</summary>
                           public class {{EndOfStream}} { }

                           """);
            if( uses_DurationMs )
                o.Append($$"""

                           /// <summary>
                           /// google.protobuf.Duration as AdHoc's elapsed time, in steps of a millisecond. An AdHoc duration is
                           /// not negative and not finer than a millisecond.
                           /// </summary>
                           public class {{DurationMs}} : Duration {
                           public TimeSpan precision => TimeSpan.FromMilliseconds(1);
                           }

                           """);

            if( 0 < packs.Length ) o.Append("\n// ═════════════════════════ packs ═════════════════════════\n").Append(packs);

            o.Append("\n// ═════════════════════════ topology ═════════════════════════\n\n");

            const string LANGS = """
                                 ///<see cref = 'InTS'/>   implementation in TypeScript
                                 ///<see cref = 'InCS'/>   implementation in C#
                                 ///<see cref = 'InJAVA'/> implementation in JAVA
                                 ///<see cref = 'InCPP'/>  implementation in C++
                                 ///<see cref = 'InRS'/>   implementation in RUST
                                 ///<see cref = 'InGO'/>   implementation in GO

                                 """;

            o.Append($"/// <summary>{(top.services.Count == 0 ? "One side of the exchange." : "Answers the calls: implements the services.")}</summary>\n").Append(LANGS);
            o.Append($"struct {Server} : Host {{ }}\n\n");
            o.Append($"/// <summary>{(top.services.Count == 0 ? "The other side of the exchange." : "Makes the calls.")}</summary>\n").Append(LANGS);
            o.Append($"struct {Client} : Host {{ }}\n\n");

            o.Append($"interface {Connection} : Connects<{Client}, {Server}> {{").Append(services);
            if( 0 < exchange.Count )
            {
                var list = exchange.Select(name_of).ToList();
                o.Append(top.services.Count == 0 ?
                             "\n// The .proto declares messages and no service: each travels on its own, either way; the state never changes.\n" :
                             "\n// The messages of the .proto that no method carries: each travels on its own, either way.\n");
                o.Append($"[_____lr_____<{(list.Count == 1 ? list[0] : "(" + string.Join(", ", list) + ")")}>]\n");
                o.Append($"struct {Exchange} {{ }}\n");
            }

            o.Append("}\n");

            if( 0 < attributes.Count )
            {
                o.Append("\n// ═════════════════════════ Protobuf metadata attributes ═════════════════════════\n\n");
                o.Append("// Not read by AdHoc: they keep what the .proto says and reach the generated code as constants.\n");
                foreach( var declaration in attributes.Values ) o.Append('\n').Append(declaration).Append('\n');
            }

            o.Append("}\n}\n");
            return tidy(o.ToString());
        }

        /// <summary>
        /// What a scope declares, in the order it is written: the project, or the container of a package. A type
        /// that comes from another file than the one shown last says which.
        /// </summary>
        void write_items(List<object> items, ProtoFile? shown, StringBuilder o)
        {
            foreach( var item in items )
                if( item is Container c )
                {
                    o.Append('\n');
                    if( name_of(c) != c.segment ) o.Append($"// `{c.segment}` in the .proto\n");
                    o.Append($"public struct {name_of(c)} {{\n");
                    write_items(c.items, null, o);
                    o.Append("}\n\n");
                }
                else
                {
                    var n = (ProtoNode)item;
                    var f = file_of(n);
                    if( f != top && f != shown ) o.Append($"\n// from {f.name}\n");
                    shown = f;
                    write(n, o);
                }
        }

        static IEnumerable<ProtoExtend> all_extends(ProtoMessage m) => m.extends.Concat(m.messages.SelectMany(all_extends));

        /// <summary>The top-level types the fields of a message, and of the messages nested in it, are typed with.</summary>
        void used_types(ProtoMessage m, HashSet<ProtoNode> typed)
        {
            foreach( var f in all_fields(m) )
                if( !is_scalar(f.type) && f.group == null && resolve(f.type, scope_of(f, m)) is { } target && top_level(target) != top_level(m) )
                    typed.Add(top_level(target));

            foreach( var n in m.messages ) used_types(n, typed);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Layout
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Lays the text out by the depth of its braces: the pieces are written flat, this indents them and keeps one
    /// blank line at most between declarations.
    /// </summary>
    static string tidy(string src)
    {
        var dst      = new StringBuilder(src.Length + src.Length / 3);
        var depth    = 0;     // { } nesting
        var parens   = 0;     // ( ) nesting
        var comment  = false; // inside /* */
        var verbatim = false; // inside @"…"
        var blank    = false; // a blank line is pending
        var prev     = "";    // the previous emitted line, trimmed

        foreach( var raw in src.Replace("\r", "").Split('\n') )
        {
            var data = verbatim; // the line continues a verbatim string
            var line = data ?
                           raw :
                           raw.Trim();

            if( !data )
            {
                if( line.Length == 0 )
                {
                    blank = true;
                    continue;
                }

                // An empty body closes on the line that opened it: `class NoArg { }`.
                if( line == "}" && prev.EndsWith('{') && !prev.StartsWith("//") && !comment )
                {
                    dst.Length--; // the line break
                    dst.Append(" }\n");
                    depth--;
                    blank = false;
                    prev  = "}";
                    continue;
                }

                // One blank line at most, and none where it only loosens the text: after an opening brace,
                // before a closing one, between a doc comment or an attribute and what it belongs to.
                if( blank && prev != "" && !prev.EndsWith('{') && line[0] != '}' && parens == 0 &&
                    (comment || !(prev.StartsWith("///") || prev.EndsWith("*/") || prev.EndsWith(")]") || prev[0] == '[' && prev[^1] == ']')) )
                    dst.Append('\n');

                var level = comment        ? line.StartsWith("*/") ? depth : depth + 1 :
                            line[0] == '}' ? depth - 1 :
                            0 < parens     ? depth + 1 :
                                             depth;
                dst.Append(' ', 4 * Math.Max(level, 0));
            }

            blank = false;
            dst.Append(line).Append('\n');
            prev = line.Trim();

            for( var i = 0; i < line.Length; i++ )
            {
                var c = line[i];
                if( verbatim )
                {
                    if( c != '"' ) continue;
                    if( i + 1 < line.Length && line[i + 1] == '"' ) i++; // "" is an escaped quote
                    else verbatim = false;
                    continue;
                }

                if( comment )
                {
                    if( c == '*' && i + 1 < line.Length && line[i + 1] == '/' )
                    {
                        comment = false;
                        i++;
                    }

                    continue;
                }

                var next = i + 1 < line.Length ?
                               line[i + 1] :
                               '\0';
                switch( c )
                {
                    case '/' when next == '/':
                        i = line.Length; // the rest of the line is a comment
                        break;
                    case '/' when next == '*':
                        comment = true;
                        i++;
                        break;
                    case '@' when next == '"':
                        verbatim = true;
                        i++;
                        break;
                    case '"': // a regular string ends on its line
                        for( i++; i < line.Length && line[i] != '"'; i++ )
                            if( line[i] == '\\' )
                                i++;
                        break;
                    case '{': depth++; break;
                    case '}': depth--; break;
                    case '(': parens++; break;
                    case ')': parens--; break;
                }
            }
        }

        return dst.ToString().TrimEnd('\n') + "\n";
    }
}
