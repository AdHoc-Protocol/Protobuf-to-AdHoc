// Protocol Buffers language reader: one .proto file as written - proto2, proto3 and editions.
// A hand-written lexer and a recursive-descent parser; nothing is resolved here, names are kept as written.
//
// Language: https://protobuf.dev/reference/protobuf/proto3-spec/ , https://protobuf.dev/reference/protobuf/proto2-spec/ ,
// https://protobuf.dev/reference/protobuf/edition-2023-spec/

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace org.unirail;

/// <summary>A syntax error in a .proto file, with the place it was found at.</summary>
public sealed class ProtoSyntaxException(string file, int line, string message) : Exception($"{file}({line}): {message}");

/// <summary>An option as written: `deprecated = true`, `(google.api.http) = { get: "/v1/items" }`.</summary>
public sealed class ProtoOption{
    public string name  = ""; // `deprecated`, `(google.api.http).get`, `features.field_presence`
    public string value = ""; // the constant: a number, an identifier, the text of a string, or `{ … }` as written
    public bool   is_string;  // the value was a string literal

    /// <summary>An option the document's authors declared themselves: its name starts with an extension in parentheses.</summary>
    public bool is_custom => name.StartsWith('(');
}

/// <summary>Anything that is declared with a name, a documentation comment and options.</summary>
public abstract class ProtoNode{
    public string            name    = "";
    public string            doc     = ""; // the comments attached to the declaration, cleaned
    public List<ProtoOption> options = [];
    public int               line;

    public string? option(string name) => options.LastOrDefault(o => o.name == name)?.value;
    public bool deprecated => option("deprecated") == "true";
}

public sealed class ProtoFile{
    public string path    = ""; // where it was read from; "" for a built-in file
    public string name    = ""; // what an `import` calls it: `google/protobuf/any.proto`
    public string syntax  = "proto2"; // proto2 | proto3 | editions
    public string edition = "";
    public string package = "";

    public List<string>      imports = [];
    public List<ProtoOption> options = [];

    // The declarations of the file, in the order they are written.
    public List<ProtoMessage> messages = [];
    public List<ProtoEnum>    enums    = [];
    public List<ProtoService> services = [];
    public List<ProtoExtend>  extends  = [];

    public string? option(string name) => options.LastOrDefault(o => o.name == name)?.value;
    public override string ToString() => name;
}

public sealed class ProtoMessage : ProtoNode{
    public ProtoFile     file = null!;
    public ProtoMessage? parent;
    public bool          is_group; // proto2 `group`: a nested message and the field of it, declared at once

    /// <summary>What the body holds, in the order it is written: fields, oneofs, nested messages and enums, notes.</summary>
    public List<object> body = [];

    public List<ProtoExtend> extends = [];

    public IEnumerable<ProtoField>   fields   => body.OfType<ProtoField>();
    public IEnumerable<ProtoOneof>   oneofs   => body.OfType<ProtoOneof>();
    public IEnumerable<ProtoMessage> messages => body.OfType<ProtoMessage>();
    public IEnumerable<ProtoEnum>    enums    => body.OfType<ProtoEnum>();

    /// <summary>The name protobuf knows the message by: its package, the messages it is nested in, its own name.</summary>
    public string full_name => (parent != null       ? parent.full_name + "." :
                                file.package != "" ? file.package       + "." :
                                                     "") + name;

    public override string ToString() => full_name;
}

/// <summary>A line of the body that declares nothing AdHoc can carry and is kept as a comment: `reserved 2, 15;`.</summary>
public sealed class ProtoNote(string text){
    public readonly string text = text;
}

public sealed class ProtoField : ProtoNode{
    public string label = ""; // "", optional, required, repeated
    public string type  = ""; // as written: `int32`, `Foo.Bar`, `.pkg.Foo`; of a map - the type of its values
    public string? map_key;   // set for `map<K, V>`: the type of the keys
    public int    number;

    public ProtoMessage? group; // the message a `group` field declares
    public ProtoOneof?   oneof;

    public bool repeated => label == "repeated";
}

public sealed class ProtoOneof : ProtoNode{
    public List<ProtoField> fields = [];
}

public sealed class ProtoEnum : ProtoNode{
    public ProtoFile     file = null!;
    public ProtoMessage? parent;

    /// <summary>The values and the notes (`reserved …`), in the order they are written.</summary>
    public List<object> body = [];

    public IEnumerable<ProtoEnumValue> values => body.OfType<ProtoEnumValue>();

    public string full_name => (parent != null       ? parent.full_name + "." :
                                file.package != "" ? file.package       + "." :
                                                     "") + name;

    public override string ToString() => full_name;
}

public sealed class ProtoEnumValue : ProtoNode{
    public long number;
}

public sealed class ProtoService : ProtoNode{
    public ProtoFile      file = null!;
    public List<ProtoRpc> rpcs = [];

    public string full_name => (file.package != "" ? file.package + "." : "") + name;
}

public sealed class ProtoRpc : ProtoNode{
    public string request  = "";
    public string response = "";
    public bool   request_stream;
    public bool   response_stream;
}

/// <summary>`extend Foo { … }`: fields added to a message declared elsewhere.</summary>
public sealed class ProtoExtend{
    public ProtoFile        file = null!;
    public ProtoMessage?    scope; // the message the block is written in; null at the level of the file
    public string           target = "";
    public List<ProtoField> fields = [];
    public int              line;
}

/// <summary>Reads one .proto file into its declarations.</summary>
public sealed class ProtoReader{
    // ───────────────────────────────────────────────────────────── tokens

    enum Kind{ Ident, Number, Text, Symbol, End }

    sealed class Comment{
        public string text = "";
        public int    first_line, last_line;
        public bool   own_line; // nothing but white space stands before it on its line
    }

    sealed class Token{
        public Kind          kind;
        public string        text = ""; // of a string literal: its value, unescaped
        public int           line;      // where it starts
        public int           last_line; // where it ends
        public int           start, end;
        public List<Comment> comments = []; // the comments between the previous token and this one
    }

    readonly string      src;
    readonly string      file;
    readonly List<Token> tokens = [];
    int                  at;

    ProtoReader(string src, string file)
    {
        this.src  = src;
        this.file = file;
        scan();
    }

    ProtoSyntaxException error(Token t, string message) => new(file, t.line, message);

    void scan()
    {
        var i        = 0;
        var line     = 1;
        var bol      = true; // only white space so far on this line
        var comments = new List<Comment>();

        while( true )
        {
            // white space
            while( i < src.Length && char.IsWhiteSpace(src[i]) || i < src.Length && src[i] == '﻿' )
            {
                if( src[i] == '\n' )
                {
                    line++;
                    bol = true;
                }

                i++;
            }

            if( i == src.Length ) break;

            var c = src[i];

            if( c == '/' && i + 1 < src.Length && src[i + 1] == '/' )
            {
                var end = src.IndexOf('\n', i);
                if( end < 0 ) end = src.Length;
                comments.Add(new Comment { text = src[i..end].TrimEnd('\r'), first_line = line, last_line = line, own_line = bol });
                i = end;
                continue;
            }

            if( c == '/' && i + 1 < src.Length && src[i + 1] == '*' )
            {
                var end = src.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if( end < 0 ) throw new ProtoSyntaxException(file, line, "a /* comment is not closed");
                var text  = src[i..(end + 2)];
                var first = line;
                line += text.Count(ch => ch == '\n');
                comments.Add(new Comment { text = text, first_line = first, last_line = line, own_line = bol });
                i   = end + 2;
                bol = false;
                continue;
            }

            var t = new Token { line = line, start = i, comments = comments };
            comments = [];
            bol      = false;

            if( char.IsLetter(c) || c == '_' )
            {
                var j = i + 1;
                while( j < src.Length && (char.IsLetterOrDigit(src[j]) || src[j] == '_') ) j++;
                t.kind = Kind.Ident;
                t.text = src[i..j];
                i      = j;
            }
            else if( char.IsDigit(c) || c == '.' && i + 1 < src.Length && char.IsDigit(src[i + 1]) )
            {
                var j = i + 1;
                while( j < src.Length && (char.IsLetterOrDigit(src[j]) || src[j] == '.' ||
                                          (src[j] == '+' || src[j] == '-') && (src[j - 1] == 'e' || src[j - 1] == 'E') && !src[i..j].StartsWith("0x", StringComparison.OrdinalIgnoreCase)) ) j++;
                t.kind = Kind.Number;
                t.text = src[i..j];
                i      = j;
            }
            else if( c == '"' || c == '\'' )
            {
                // A literal of protobuf is a sequence of bytes: an octal or a hexadecimal escape is one byte of it,
                // so `"\341\210\264"` is one character written as its UTF-8.
                var bytes = new List<byte>();
                void put(string s) => bytes.AddRange(Encoding.UTF8.GetBytes(s));

                var j = i + 1;
                for( ;; j++ )
                {
                    if( j >= src.Length || src[j] == '\n' ) throw new ProtoSyntaxException(file, line, "a string literal is not closed");
                    if( src[j] == c ) break;
                    if( src[j] != '\\' )
                    {
                        if( char.IsHighSurrogate(src[j]) && j + 1 < src.Length && char.IsLowSurrogate(src[j + 1]) ) put(src.Substring(j++, 2));
                        else put(src[j].ToString());
                        continue;
                    }

                    j++;
                    if( j >= src.Length ) throw new ProtoSyntaxException(file, line, "a string literal is not closed");
                    switch( src[j] )
                    {
                        case 'n': bytes.Add((byte)'\n'); break;
                        case 'r': bytes.Add((byte)'\r'); break;
                        case 't': bytes.Add((byte)'\t'); break;
                        case 'a': bytes.Add((byte)'\a'); break;
                        case 'b': bytes.Add((byte)'\b'); break;
                        case 'f': bytes.Add((byte)'\f'); break;
                        case 'v': bytes.Add((byte)'\v'); break;
                        case 'x' or 'X':
                        {
                            var k = j + 1;
                            while( k < src.Length && k < j + 3 && Uri.IsHexDigit(src[k]) ) k++;
                            if( k == j + 1 ) put(src[j].ToString());
                            else bytes.Add(Convert.ToByte(src[(j + 1)..k], 16));
                            j = k - 1;
                            break;
                        }
                        case 'u' or 'U':
                        {
                            var n = src[j] == 'u' ? 4 : 8;
                            if( j + n < src.Length && src[(j + 1)..(j + 1 + n)].All(Uri.IsHexDigit) &&
                                Convert.ToInt32(src[(j + 1)..(j + 1 + n)], 16) is var code and (< 0xD800 or > 0xDFFF and <= 0x10FFFF) )
                            {
                                put(char.ConvertFromUtf32(code));
                                j += n;
                            }
                            else put(src[j].ToString());

                            break;
                        }
                        case >= '0' and <= '7':
                        {
                            var k = j;
                            while( k < src.Length && k < j + 3 && src[k] is >= '0' and <= '7' ) k++;
                            bytes.Add((byte)Convert.ToInt32(src[j..k], 8));
                            j = k - 1;
                            break;
                        }
                        default: put(src[j].ToString()); break; // \ \' \" \?
                    }
                }

                // Text when the bytes are text; otherwise each byte as the character of its code, so none is lost.
                var raw = bytes.ToArray();
                try { t.text = new UTF8Encoding(false, true).GetString(raw); }
                catch( ArgumentException ) { t.text = string.Concat(raw.Select(b => (char)b)); }

                t.kind = Kind.Text;
                i      = j + 1;
            }
            else
            {
                t.kind = Kind.Symbol;
                t.text = c.ToString();
                i++;
            }

            t.end       = i;
            t.last_line = line;
            tokens.Add(t);
        }

        tokens.Add(new Token { kind = Kind.End, line = line, last_line = line, start = src.Length, end = src.Length, comments = comments });
    }

    // ───────────────────────────────────────────────────────────── comments

    /// <summary>The text of a comment without its markers: `//`, `///`, `/*`, `*/`, the `*` that leads a line of a block.</summary>
    static string clean(Comment c)
    {
        var t = c.text;
        if( t.StartsWith("//") ) return t.TrimStart('/').TrimEnd();

        t = t[2..^2];
        if( t.StartsWith('*') ) t = t[1..];
        var lines = t.Replace("\r", "").Split('\n')
                     .Select(l =>
                             {
                                 var s = l.TrimStart();
                                 return s.StartsWith('*') ? s[1..] : l;
                             }).ToList();
        return string.Join("\n", lines).Trim('\n');
    }

    /// <summary>Lines of comments joined, with the indent they share removed.</summary>
    static string join(IEnumerable<string> parts)
    {
        var lines = parts.SelectMany(p => p.Split('\n')).Select(l => l.TrimEnd()).ToList();
        var indent = lines.Where(l => l.Length > 0).Select(l => l.Length - l.TrimStart().Length).DefaultIfEmpty(0).Min();
        return string.Join("\n", lines.Select(l => l.Length >= indent ? l[indent..] : l)).Trim('\n', ' ');
    }

    /// <summary>
    /// The comment that documents a declaration starting at token <paramref name="first"/>: the comments right above
    /// it with no blank line in between. A comment that ends the line of the previous declaration is that one's.
    /// </summary>
    string leading(int first)
    {
        var t        = tokens[first];
        var previous = first == 0 ? -1 : tokens[first - 1].last_line;
        var taken    = new List<Comment>();
        var below    = t.line;

        for( var i = t.comments.Count - 1; 0 <= i; i-- )
        {
            var c = t.comments[i];
            if( c.first_line == previous && !c.own_line ) break;                 // trails the previous declaration
            if( c.last_line < below - 1 ) break;                                 // a blank line: a detached comment
            taken.Insert(0, c);
            below = c.first_line;
        }

        return join(taken.Select(clean));
    }

    /// <summary>The comment that follows a declaration on its last line; <paramref name="last"/> is its last token.</summary>
    string trailing(int last)
    {
        if( last + 1 >= tokens.Count ) return "";
        var line = tokens[last].last_line;
        var c    = tokens[last + 1].comments.FirstOrDefault();
        return c != null && c.first_line == line && !c.own_line ? join([clean(c)]) : "";
    }

    static string doc(string leading, string trailing) => leading == "" ? trailing : trailing == "" ? leading : leading + "\n" + trailing;

    // ───────────────────────────────────────────────────────────── parser helpers

    Token peek(int ahead = 0) => tokens[Math.Min(at + ahead, tokens.Count - 1)];
    Token next() => tokens[at < tokens.Count - 1 ? at++ : at];

    bool is_symbol(string s, int ahead = 0) => peek(ahead).kind == Kind.Symbol && peek(ahead).text == s;
    bool is_ident(int ahead = 0) => peek(ahead).kind == Kind.Ident;
    bool is_word(string s, int ahead = 0) => peek(ahead).kind == Kind.Ident && peek(ahead).text == s;

    bool accept(string symbol)
    {
        if( !is_symbol(symbol) ) return false;
        at++;
        return true;
    }

    void expect(string symbol)
    {
        if( !accept(symbol) ) throw error(peek(), $"`{symbol}` expected, `{peek().text}` found");
    }

    string ident(string what)
    {
        if( !is_ident() ) throw error(peek(), $"{what} expected, `{peek().text}` found");
        return next().text;
    }

    /// <summary>A name with dots: `foo.bar.Baz`, `.foo.Bar`.</summary>
    string full_ident(string what)
    {
        var sb = new StringBuilder();
        if( accept(".") ) sb.Append('.');
        sb.Append(ident(what));
        while( is_symbol(".") && is_ident(1) )
        {
            at++;
            sb.Append('.').Append(next().text);
        }

        return sb.ToString();
    }

    static long integer(Token t, string file)
    {
        var s = t.text;
        try
        {
            if( s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ) return Convert.ToInt64(s[2..], 16);
            if( 1 < s.Length && s[0] == '0' && s.All(char.IsDigit) ) return Convert.ToInt64(s, 8);
            return long.Parse(s, CultureInfo.InvariantCulture);
        }
        catch( Exception ) { throw new ProtoSyntaxException(file, t.line, $"`{s}` is not an integer"); }
    }

    /// <summary>The source between two tokens as one line.</summary>
    string text_between(int first, int last) => string.Join(' ', src[tokens[first].start..tokens[last].end].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    // ───────────────────────────────────────────────────────────── options

    /// <summary>`deprecated`, `(my.option).field`, `features.field_presence`</summary>
    string option_name()
    {
        var sb = new StringBuilder();
        while( true )
        {
            if( accept("(") )
            {
                sb.Append('(').Append(full_ident("the name of an option")).Append(')');
                expect(")");
            }
            else sb.Append(ident("the name of an option"));

            if( !accept(".") ) break;
            sb.Append('.');
        }

        return sb.ToString();
    }

    /// <summary>The value of an option: a constant, or a message literal `{ … }` kept as it is written.</summary>
    void option_value(ProtoOption o)
    {
        if( is_symbol("{") )
        {
            var first = at;
            var depth = 0;
            do
            {
                if( peek().kind == Kind.End ) throw error(tokens[first], "the `{` of an option value is not closed");
                if( is_symbol("{") ) depth++;
                else if( is_symbol("}") ) depth--;
                at++;
            }
            while( 0 < depth );

            o.value = text_between(first, at - 1);
            return;
        }

        if( peek().kind == Kind.Text )
        {
            var sb = new StringBuilder();
            while( peek().kind == Kind.Text ) sb.Append(next().text); // adjacent literals are one string
            o.value     = sb.ToString();
            o.is_string = true;
            return;
        }

        var sign = is_symbol("-") || is_symbol("+") ? next().text : "";
        if( sign == "+" ) sign = "";
        if( peek().kind == Kind.Number ) o.value = sign + next().text;
        else if( is_ident() ) o.value            = sign + full_ident("a value");
        else throw error(peek(), $"the value of the option `{o.name}` expected, `{peek().text}` found");
    }

    /// <summary>`option name = value;` - the word `option` is already taken.</summary>
    ProtoOption option_statement()
    {
        var o = new ProtoOption { name = option_name() };
        expect("=");
        option_value(o);
        expect(";");
        return o;
    }

    /// <summary>`[name = value, …]` after a field or an enum value, when it is there.</summary>
    List<ProtoOption> bracket_options()
    {
        var list = new List<ProtoOption>();
        if( !accept("[") ) return list;
        do
        {
            if( is_symbol("]") ) break; // a trailing comma
            var o = new ProtoOption { name = option_name() };
            expect("=");
            option_value(o);
            list.Add(o);
        }
        while( accept(",") );

        expect("]");
        return list;
    }

    // ───────────────────────────────────────────────────────────── declarations

    public static ProtoFile read(string src, string path, string name)
    {
        var r = new ProtoReader(src, path == "" ? name : path);
        var f = new ProtoFile { path = path, name = name };
        r.file_body(f);
        return f;
    }

    void file_body(ProtoFile f)
    {
        while( peek().kind != Kind.End )
        {
            if( accept(";") ) continue;
            var first = at;
            var word  = ident("a declaration");

            switch( word )
            {
                case "syntax":
                    expect("=");
                    f.syntax = peek().kind == Kind.Text ? next().text : throw error(peek(), "the syntax expected: \"proto2\" or \"proto3\"");
                    expect(";");
                    break;
                case "edition":
                    expect("=");
                    f.edition = peek().kind == Kind.Text ? next().text : throw error(peek(), "the edition expected: \"2023\"");
                    f.syntax  = "editions";
                    expect(";");
                    break;
                case "import":
                    if( is_word("public") || is_word("weak") || is_word("option") ) at++;
                    f.imports.Add(peek().kind == Kind.Text ? next().text : throw error(peek(), "the path of the imported file expected"));
                    expect(";");
                    break;
                case "package":
                    f.package = full_ident("the name of the package");
                    expect(";");
                    break;
                case "option":
                    f.options.Add(option_statement());
                    break;
                case "message":
                    f.messages.Add(message(f, null, first));
                    break;
                case "enum":
                    f.enums.Add(enumeration(f, null, first));
                    break;
                case "service":
                    f.services.Add(service(f, first));
                    break;
                case "extend":
                    f.extends.Add(extend(f, null, first));
                    break;
                default: throw error(tokens[first], $"`{word}` is not a declaration a file may hold");
            }
        }
    }

    /// <summary>`message Name { … }` - the word `message` is already taken; <paramref name="first"/> is its token.</summary>
    ProtoMessage message(ProtoFile f, ProtoMessage? parent, int first)
    {
        var m = new ProtoMessage { file = f, parent = parent, line = tokens[first].line, name = ident("the name of the message") };
        var lead = leading(first);
        expect("{");
        m.doc = doc(lead, trailing(at - 1));
        message_body(m);
        return m;
    }

    /// <summary>The body of a message or of a group, up to and including its `}`.</summary>
    void message_body(ProtoMessage m)
    {
        while( !accept("}") )
        {
            if( peek().kind == Kind.End ) throw error(peek(), $"the `{{` of `{m.name}` is not closed");
            if( accept(";") ) continue;

            var first = at;
            if( is_word("message") && is_ident(1) && is_symbol("{", 2) )
            {
                at++;
                m.body.Add(message(m.file, m, first));
            }
            else if( is_word("enum") && is_ident(1) && is_symbol("{", 2) )
            {
                at++;
                m.body.Add(enumeration(m.file, m, first));
            }
            else if( is_word("oneof") && is_ident(1) && is_symbol("{", 2) )
            {
                at++;
                m.body.Add(oneof(m, first));
            }
            else if( is_word("extend") && (is_ident(1) || is_symbol(".", 1)) )
            {
                at++;
                m.extends.Add(extend(m.file, m, first));
            }
            else if( is_word("option") && (is_ident(1) || is_symbol("(", 1)) ) // a keyword here, as protoc reads it
            {
                at++;
                m.options.Add(option_statement());
            }
            else if( (is_word("reserved") || is_word("extensions")) && !is_ident(1) || is_word("reserved") && is_ident(1) && !is_symbol("=", 2) )
            {
                // `reserved 2, 15, 9 to 11;`  `reserved "foo";`  `reserved foo, bar;` (editions)  `extensions 100 to max;`
                while( !is_symbol(";") && peek().kind != Kind.End ) at++;
                m.body.Add(new ProtoNote(text_between(first, at - 1)));
                expect(";");
            }
            else field(m, null, first);
        }
    }

    /// <summary>A field, a map field or a group, added to the body of its message (and to its oneof).</summary>
    void field(ProtoMessage m, ProtoOneof? oneof, int first)
    {
        var fld  = new ProtoField { line = tokens[first].line, oneof = oneof };
        var lead = leading(first);

        if( (is_word("optional") || is_word("required") || is_word("repeated")) && (is_ident(1) || is_symbol(".", 1)) && !is_symbol("=", 2) )
            fld.label = next().text;

        if( is_word("group") && is_ident(1) && is_symbol("=", 2) )
        {
            at++;
            var g = new ProtoMessage { file = m.file, parent = m, is_group = true, line = fld.line, name = ident("the name of the group") };
            expect("=");
            fld.number  = (int)integer(peek().kind == Kind.Number ? next() : throw error(peek(), "the number of the field expected"), file);
            fld.options = bracket_options();
            expect("{");
            g.doc = doc(lead, trailing(at - 1)); // said once, of the message
            message_body(g);

            fld.type  = g.name;
            fld.name  = g.name.ToLowerInvariant(); // protobuf's own rule for the field of a group
            fld.group = g;
        }
        else
        {
            if( is_word("map") && is_symbol("<", 1) )
            {
                at += 2;
                fld.map_key = full_ident("the type of the keys");
                expect(",");
                fld.type = full_ident("the type of the values");
                expect(">");
            }
            else fld.type = full_ident("the type of the field");

            fld.name = ident("the name of the field");
            expect("=");
            fld.number  = (int)integer(peek().kind == Kind.Number ? next() : throw error(peek(), "the number of the field expected"), file);
            fld.options = bracket_options();
            expect(";");
            fld.doc = doc(lead, trailing(at - 1));
        }

        // The message of a group stands before the field of it: the type, then what is typed with it.
        if( fld.group != null ) m.body.Add(fld.group);
        if( oneof != null ) oneof.fields.Add(fld);
        else m.body.Add(fld);
    }

    ProtoOneof oneof(ProtoMessage m, int first)
    {
        var o    = new ProtoOneof { line = tokens[first].line, name = ident("the name of the oneof") };
        var lead = leading(first);
        expect("{");
        o.doc = doc(lead, trailing(at - 1));

        while( !accept("}") )
        {
            if( peek().kind == Kind.End ) throw error(peek(), $"the `{{` of `{o.name}` is not closed");
            if( accept(";") ) continue;

            if( is_word("option") && (is_ident(1) || is_symbol("(", 1)) )
            {
                at++;
                o.options.Add(option_statement());
            }
            else field(m, o, at);
        }

        return o;
    }

    ProtoEnum enumeration(ProtoFile f, ProtoMessage? parent, int first)
    {
        var e    = new ProtoEnum { file = f, parent = parent, line = tokens[first].line, name = ident("the name of the enum") };
        var lead = leading(first);
        expect("{");
        e.doc = doc(lead, trailing(at - 1));

        while( !accept("}") )
        {
            if( peek().kind == Kind.End ) throw error(peek(), $"the `{{` of `{e.name}` is not closed");
            if( accept(";") ) continue;

            var start = at;
            if( is_word("option") && !is_symbol("=", 1) )
            {
                at++;
                e.options.Add(option_statement());
            }
            else if( is_word("reserved") && !is_symbol("=", 1) )
            {
                while( !is_symbol(";") && peek().kind != Kind.End ) at++;
                e.body.Add(new ProtoNote(text_between(start, at - 1)));
                expect(";");
            }
            else
            {
                var v     = new ProtoEnumValue { line = peek().line };
                var vlead = leading(start);
                v.name = ident("the name of the value");
                expect("=");
                var negative = accept("-");
                v.number = integer(peek().kind == Kind.Number ? next() : throw error(peek(), "the number of the value expected"), file);
                if( negative ) v.number = -v.number;
                v.options = bracket_options();
                expect(";");
                v.doc = doc(vlead, trailing(at - 1));
                e.body.Add(v);
            }
        }

        return e;
    }

    ProtoService service(ProtoFile f, int first)
    {
        var s    = new ProtoService { file = f, line = tokens[first].line, name = ident("the name of the service") };
        var lead = leading(first);
        expect("{");
        s.doc = doc(lead, trailing(at - 1));

        while( !accept("}") )
        {
            if( peek().kind == Kind.End ) throw error(peek(), $"the `{{` of `{s.name}` is not closed");
            if( accept(";") ) continue;

            var start = at;
            var word  = ident("`rpc` or `option`");
            if( word == "option" )
            {
                s.options.Add(option_statement());
                continue;
            }

            if( word != "rpc" ) throw error(tokens[start], $"`rpc` or `option` expected, `{word}` found");

            var rpc   = new ProtoRpc { line = tokens[start].line };
            var rlead = leading(start);
            rpc.name = ident("the name of the method");

            expect("(");
            if( is_word("stream") && !is_symbol(")", 1) && !is_symbol(".", 1) )
            {
                at++;
                rpc.request_stream = true;
            }

            rpc.request = full_ident("the type of the request");
            expect(")");

            var returns = at;
            if( ident("`returns`") != "returns" ) throw error(tokens[returns], $"`returns` expected, `{tokens[returns].text}` found");

            expect("(");
            if( is_word("stream") && !is_symbol(")", 1) && !is_symbol(".", 1) )
            {
                at++;
                rpc.response_stream = true;
            }

            rpc.response = full_ident("the type of the response");
            expect(")");

            var last = at - 1;
            if( accept("{") )
            {
                last = at - 1;
                while( !accept("}") )
                {
                    if( peek().kind == Kind.End ) throw error(peek(), $"the `{{` of `{rpc.name}` is not closed");
                    if( accept(";") ) continue;
                    if( ident("`option`") != "option" ) throw error(peek(), "only options may stand in the body of a method");
                    rpc.options.Add(option_statement());
                }

                accept(";");
            }
            else
            {
                expect(";");
                last = at - 1;
            }

            rpc.doc = doc(rlead, trailing(last));
            s.rpcs.Add(rpc);
        }

        return s;
    }

    ProtoExtend extend(ProtoFile f, ProtoMessage? scope, int first)
    {
        var x = new ProtoExtend { file = f, scope = scope, line = tokens[first].line, target = full_ident("the message to extend") };
        expect("{");

        // The fields are read as the body of a message of their own, so a group among them is read too.
        var holder = new ProtoMessage { file = f, parent = scope, name = "" };
        while( !accept("}") )
        {
            if( peek().kind == Kind.End ) throw error(peek(), "the `{` of `extend` is not closed");
            if( accept(";") ) continue;
            field(holder, null, at);
        }

        x.fields.AddRange(holder.fields);
        foreach( var g in holder.messages ) // a group declared by an extension lives where the block is written
            if( scope != null ) scope.body.Add(g);
            else
            {
                g.parent = null;
                f.messages.Add(g);
            }

        return x;
    }
}
