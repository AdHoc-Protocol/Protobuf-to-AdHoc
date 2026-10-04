# Protobuf_to_AdHoc

Converts **Protocol Buffers** schemas (`.proto`: proto2, proto3 and editions) into
[AdHoc protocol](https://github.com/AdHoc-Protocol/AdHoc-protocol) descriptions (`.cs`).

The package becomes the namespace, a message a pack, a service an interface in a connection, an `rpc` an RPC method, a
streaming `rpc` becomes an actor. What the `.proto` says about the *values* is kept as AdHoc says it: the varint
its author chose for a field becomes `[X]` or `[A]`, a `Timestamp` becomes a `DateTime`, a wrapper becomes `T?`,
a `oneof` becomes a pack of optional alternatives. What describes protobuf's own wire - field numbers, packing -
is left behind: AdHoc lays out its own wire.

The converter is one C# program with no dependencies: it has its own reader of the protobuf language, runs
locally and sends nothing anywhere. It is the converter that used to be built into AdHocAgent and the code
generator, now a project of its own and a member of the
[family of converters](https://github.com/AdHoc-Protocol#converters-to-adhoc-protocol): it shares their
conventions ([CONVENTIONS.md](CONVENTIONS.md)) and references nothing outside this folder.

## Links

- Protocol Buffers, the language: [proto3](https://protobuf.dev/programming-guides/proto3/),
  [proto2](https://protobuf.dev/programming-guides/proto2/),
  [editions](https://protobuf.dev/programming-guides/editions/); the grammar:
  [proto3](https://protobuf.dev/reference/protobuf/proto3-spec/),
  [proto2](https://protobuf.dev/reference/protobuf/proto2-spec/),
  [edition 2023](https://protobuf.dev/reference/protobuf/edition-2023-spec/)
- The well-known types: https://protobuf.dev/reference/protobuf/google.protobuf/
- gRPC, the four kinds of a method: https://grpc.io/docs/what-is-grpc/core-concepts/
- The reference implementation of the language, `protoc`: https://github.com/protocolbuffers/protobuf
- AdHoc protocol, the manual: https://github.com/AdHoc-Protocol/AdHoc-protocol

Where the samples come from (`fetch-samples.sh` downloads them, the upstream layout is kept so that imports
resolve). [`samples/sources.txt`](samples/sources.txt) gives the original page of every file, and the header of
every description links the originals it was made from:

| Folder of `samples/` | Source |
|:--|:--|
| `grpc/` | gRPC examples, the health checking protocol and server reflection: https://github.com/grpc/grpc/tree/master/examples/protos · https://github.com/grpc/grpc-proto |
| `protobuf/` | protobuf's own schema, its test schemas and its example: https://github.com/protocolbuffers/protobuf |
| `googleapis/` | common types of Google APIs (status, error details, date, money), ByteStream, HTTP annotations: https://github.com/googleapis/googleapis |
| `etcd/` | the etcd v3 API: https://github.com/etcd-io/etcd/tree/main/api |
| `containerd/` | containerd services (containers, events): https://github.com/containerd/containerd/tree/main/api |
| `opentelemetry/` | OpenTelemetry (OTLP): traces, metrics, logs and the services that export them: https://github.com/open-telemetry/opentelemetry-proto |
| `kubernetes/` | the Container Runtime Interface, between the kubelet and a container runtime: https://github.com/kubernetes/cri-api |
| `arrow/` | Apache Arrow Flight: https://github.com/apache/arrow/blob/main/format/Flight.proto |
| `cloudevents/` | the CloudEvents format: https://github.com/cloudevents/spec |

## Before and after

`route_guide.proto` of the gRPC examples (111 lines) and the `AdHoc/route_guide.cs` it generates (193 lines),
both abridged here. Full files: [the original](https://github.com/grpc/grpc/blob/master/examples/protos/route_guide.proto)
and [the result](AdHoc/route_guide.cs).

**Protocol Buffers**

```protobuf
service RouteGuide {
  rpc GetFeature(Point) returns (Feature) {}
  rpc ListFeatures(Rectangle) returns (stream Feature) {}
  rpc RecordRoute(stream Point) returns (RouteSummary) {}
  rpc RouteChat(stream RouteNote) returns (stream RouteNote) {}
}

message Point {
  int32 latitude = 1;
  int32 longitude = 2;
}

message Feature {
  // The name of the feature.
  string name = 1;

  // The point where the feature is detected.
  Point location = 2;
}
```

**AdHoc protocol description**

```csharp
namespace routeguide {                                     // the package
    public interface route_guide {                         // the file
        public class Point {
            [X] int latitude;                              // `int32` is a varint: [X] spends the saving on both signs
            [X] int longitude;
        }

        public class Feature {
            /// <summary>The name of the feature.</summary>
            string name;

            /// <summary>The point where the feature is detected.</summary>
            Point location;
        }

        interface ClientServerConnection : Connects<Client, Server> {
            public interface RouteGuide {                  // the service
                (L____________, Feature) GetFeature(Point req);

                public interface ListFeatures : Actor {    // the answer is a stream
                    int MaxActiveInstances => UNLIMITED;

                    [L____________<Return_ListFeatures, Rectangle>]
                    struct Call_ListFeatures { }

                    [____________r<Feature>]               // any number of items, the state stays
                    [____________R<End, EndOfStream>]      // no more of them
                    struct Return_ListFeatures { }
                }

                public interface RouteChat : Actor {       // a stream both ways
                    int MaxActiveInstances => UNLIMITED;

                    [l____________<RouteNote>]
                    [____________r<RouteNote>]
                    [L____________<Drain_RouteChat, EndOfStream>]  // the caller has no more to say
                    struct Call_RouteChat { }

                    [____________r<RouteNote>]
                    [____________R<End, EndOfStream>]
                    struct Drain_RouteChat { }
                }
            }
        }
    }
}
```

A plain call became an RPC method: the Client (Left) calls, the Server answers. A streaming call became an actor
whose states say who may send what and when the call is over.

gRPC knows the first method as `/routeguide.RouteGuide/GetFeature`. The description says the same without writing
that path anywhere: the package is its namespace, the service is an interface in the connection, the method is
declared in it. A hierarchy of names is carried over as a hierarchy of declarations, never as a text beside them.

## Real protocols, converted

The samples are schemas that run in production. Each row leads to the original schema in its repository and to
the description generated from it; all of them pass the agent's check.

| Project | Schema | Description | What to look at |
|:--|:--|:--|:--|
| OpenTelemetry (OTLP) | [trace_service.proto](https://github.com/open-telemetry/opentelemetry-proto/blob/main/opentelemetry/proto/collector/trace/v1/trace_service.proto), [metrics_service.proto](https://github.com/open-telemetry/opentelemetry-proto/blob/main/opentelemetry/proto/collector/metrics/v1/metrics_service.proto), [logs_service.proto](https://github.com/open-telemetry/opentelemetry-proto/blob/main/opentelemetry/proto/collector/logs/v1/logs_service.proto) | [trace_service.cs](AdHoc/trace_service.cs), [metrics_service.cs](AdHoc/metrics_service.cs), [logs_service.cs](AdHoc/logs_service.cs) | a service of one package that carries messages of three others; the signals themselves are in [trace.cs](AdHoc/trace.cs), [metrics.cs](AdHoc/metrics.cs), [logs.cs](AdHoc/logs.cs) |
| Kubernetes CRI | [api.proto](https://github.com/kubernetes/cri-api/blob/master/pkg/apis/runtime/v1/api.proto) | [api.cs](AdHoc/api.cs) | 2 398 lines, two services, 36 RPC methods and 7 streams between the kubelet and a container runtime |
| etcd v3 | [rpc.proto](https://github.com/etcd-io/etcd/blob/main/api/etcdserverpb/rpc.proto) | [rpc.cs](AdHoc/rpc.cs) | six services, 38 RPC methods, `Watch` and `LeaseKeepAlive` as two-way streams, types of two more packages |
| Apache Arrow Flight | [Flight.proto](https://github.com/apache/arrow/blob/main/format/Flight.proto) | [Flight.cs](AdHoc/Flight.cs) | seven of ten methods stream, three of them both ways |
| containerd | [containers.proto](https://github.com/containerd/containerd/blob/main/api/services/containers/v1/containers.proto), [events.proto](https://github.com/containerd/containerd/blob/main/api/services/events/v1/events.proto) | [containers.cs](AdHoc/containers.cs), [events.cs](AdHoc/events.cs) | calls that answer with `Empty` as one-way calls, a subscription as a stream |
| gRPC | [route_guide.proto](https://github.com/grpc/grpc/blob/master/examples/protos/route_guide.proto), [health.proto](https://github.com/grpc/grpc-proto/blob/master/grpc/health/v1/health.proto), [reflection.proto](https://github.com/grpc/grpc-proto/blob/master/grpc/reflection/v1/reflection.proto) | [route_guide.cs](AdHoc/route_guide.cs), [health.cs](AdHoc/health.cs), [reflection.cs](AdHoc/reflection.cs) | the four kinds of a method; a oneof in a request |
| CloudEvents | [cloudevents.proto](https://github.com/cloudevents/spec/blob/main/cloudevents/formats/cloudevents.proto) | [cloudevents.cs](AdHoc/cloudevents.cs) | an envelope: a map of attribute values that are a oneof, a oneof of payloads |
| Google APIs | [bytestream.proto](https://github.com/googleapis/googleapis/blob/master/google/bytestream/bytestream.proto), [error_details.proto](https://github.com/googleapis/googleapis/blob/master/google/rpc/error_details.proto) | [bytestream.cs](AdHoc/bytestream.cs), [error_details.cs](AdHoc/error_details.cs) | streams of bytes; `Duration` |
| Protocol Buffers | [descriptor.proto](https://github.com/protocolbuffers/protobuf/blob/main/src/google/protobuf/descriptor.proto), [unittest.proto](https://github.com/protocolbuffers/protobuf/blob/main/src/google/protobuf/unittest.proto) | [descriptor.cs](AdHoc/descriptor.cs), [unittest.cs](AdHoc/unittest.cs) | proto2 and an edition: groups, extensions, defaults, every scalar type |

OpenTelemetry's trace export, the way every collector receives spans:

```protobuf
package opentelemetry.proto.collector.trace.v1;

import "opentelemetry/proto/trace/v1/trace.proto";

service TraceService {
  rpc Export(ExportTraceServiceRequest) returns (ExportTraceServiceResponse) {}
}

message ExportTraceServiceRequest {
  repeated opentelemetry.proto.trace.v1.ResourceSpans resource_spans = 1;
}
```

```csharp
namespace opentelemetry.proto.collector.trace.v1 {         // the package of the file
    public interface trace_service {
        public class ExportTraceServiceRequest {
            opentelemetry.proto.trace.v1.ResourceSpans[,,] resource_spans;
        }

        public struct opentelemetry {                      // the packages of what the file imports
            public struct proto {
                public struct trace {
                    public struct v1 {
                        public class ResourceSpans { ... }
                        public class Span { ... }
                    }
                }

                public struct resource { public struct v1 { public class Resource { ... } } }
                public struct common { public struct v1 { public class KeyValue { ... } } }
            }
        }

        interface ClientServerConnection : Connects<Client, Server> {
            public interface TraceService {
                (L____________, ExportTraceServiceResponse) Export(ExportTraceServiceRequest req);
            }
        }
    }
}
```

The file is 77 lines and declares one method; the description is 711 lines, because what the method carries -
spans, resources, attribute values - is declared in three imported packages and is written out with it, each
type where its package puts it.

## Layout

| Path | Contents |
|:--|:--|
| `src/Protobuf2AdHoc.cs` | The converter: files and imports, name resolution, the name pass, the emitter |
| `src/ProtoReader.cs` | The reader of the protobuf language: a lexer and a recursive-descent parser |
| `src/Originals.cs` | Reads `sources.txt`: the original page of an input file, for the header of its description |
| `src/adhoc/AdHocNames.cs` | Local copy of AdHocAgent's keyword list and its `brush` rename rule |
| `Protobuf2AdHoc.csproj` | .NET 10 console project; compiles `src/` only; no packages |
| `fetch-samples.sh`, `samples/` | 40 real `.proto` files, the script that downloads them and `samples/sources.txt`, the original page of each |
| `build.sh` | Builds the converter into `out/` and regenerates `AdHoc/` from `samples/` |
| `AdHoc/*.cs` | Generated descriptions, one per `.proto` that has something to send, kept for review |
| `validate.sh` | AdHocAgent parse-only check of a folder of descriptions |
| `tests/` | Synthetic schemas - every construct once, every edge case, the editions syntax - with their descriptions |
| `CONVENTIONS.md` | The rules every converter of the family follows |

## What gets generated

For each `<name>.proto` the converter writes `<name>.cs` (`namespace <package>`, `interface <name>`). Its header
names the file and the imported files it takes types from, each with the page of its original when a
`sources.txt` in a folder above lists it (`fetch-samples.sh` writes one for the samples; a path ending with `/`
there stands for every file under that folder). Then, in order:

1. **Packs Inventory (Dashboard)** - one `<see cref='…'/>` line per pack that travels on its own, pre-tagged
   with the services that carry it, so branches can route by tag (`KeepDoc`). No `id` is pinned: pack ids are
   AdHoc's own and AdHocAgent assigns them.
2. **Notes** on what only the developer can decide: `long` against `longJS`, where `[X]` / `[A]` do not fit,
   `bytes` as a value against a conduit.
3. **`_DefaultMaxLengthOf`** with permissive caps (65 535), and what the converter declares itself when the file
   needs it: `NoArg` (protobuf's `Empty`), `EndOfStream`, `DurationMs`.
4. **Packs** - the messages and enums of the file in the order they are written, then the types of imported
   files that these are typed with: those of the same package beside them, those of another package in
   containers named after it (`google.rpc.Status`).
5. **Hosts** - `Server` and `Client`; both request all six language generators.
6. **The connection** - `ClientServerConnection : Connects<Client, Server>` with one interface per service, and
   the state `Exchange` for the messages no method carries.
7. **Attribute declarations** - the custom attribute classes used above. AdHoc turns them into constants of the
   generated code; it does not read them.

## How protobuf maps to AdHoc

### Scalar types

Protobuf has several types for one range of values, and which one the author picked is a statement about the
values: a varint is chosen where small numbers are the common ones, a fixed width where they are not.

| Protobuf | AdHoc | Notes |
|:--|:--|:--|
| `int32`, `sint32` | `[X] int` | a varint: `[X]` gives small values of either sign few bytes |
| `int64`, `sint64` | `[X] long` | |
| `uint32` | `[A] uint` | an unsigned varint: values cluster at the low end |
| `uint64` | `[A] ulong` | |
| `fixed32`, `fixed64` | `uint`, `ulong` | fixed width: no attribute |
| `sfixed32`, `sfixed64` | `int`, `long` | |
| `float`, `double`, `bool`, `string` | the same | |
| `bytes` | `Binary[,,]` | a value held whole; a BLOB is better declared `[S(N)] File`, the file says so where it opens |

A varint loses on values that are systematically large - an identifier, a hash, a coordinate scaled by 1e7:
there the attribute is to be dropped by hand, and the generated file says so where it opens.

### Messages

| Protobuf | AdHoc | Notes |
|:--|:--|:--|
| `message` | `public class` | nested messages and enums stay nested |
| `package` of the file | the `namespace` of the description | a package is what a namespace is called in protobuf; a file without one gets `org.protobuf` |
| a type of another package | nested containers named after that package: `public struct google { public struct rpc { ... } }` | referred to as protobuf does it, `google.rpc.Status` |
| `repeated T` | `T[,,]` | a list whose length travels with it; `repeated bytes` is `Binary[,,][,,]` |
| `map<K, V>` | `Map<K, V>` | the varint of a key or a value as `[Key: X]`, `[Val: A]` |
| `map<bool, V>` | two fields, `<name>_false` and `<name>_true` | a map keyed by a boolean holds two values at most: two fields say it plainly |
| `optional` (proto2, proto3) | `T?` | |
| `required`, a proto3 field without a label | `T` | |
| an edition field without a label | `T?`, or `T` when `features.field_presence` is `IMPLICIT` or `LEGACY_REQUIRED` | an edition gives a field presence by default |
| `oneof name { … }` | a nested pack `Name` of optional alternatives and one field `Name name;` | what a oneof is on the wire; that exactly one alternative is set is stated in the doc |
| `group` (proto2) | a nested pack and a field of it | the field is named as protobuf names it: the name of the group in lower case |
| `extend Message { … }` | the fields are added to that message, under a comment that says where they were declared | an extension is a field of the message it extends; `extend` of an options message declares an option and is not carried |
| `enum` | `public enum`, values as they are | an enum with one value: a `struct` of constants, a field of it is `[X] int` |
| `reserved`, `extensions` | a comment at the place | |
| field number | nothing | it is protobuf's wire: AdHoc sends fields in the order they are declared, and the order of the `.proto` is kept |
| a comment above a declaration or after it on its line | the doc comment | a comment separated by a blank line is not attached, as in `protoc` |

### Well-known types

| Protobuf | AdHoc | Notes |
|:--|:--|:--|
| `google.protobuf.Timestamp` | `DateTime` | |
| `google.protobuf.Duration` | `DurationMs`, an alias of AdHoc's `Duration` in steps of 1 ms | an AdHoc duration is not negative and not finer than a millisecond |
| `google.protobuf.Empty` | `NoArg` as a request or a reply; `bool` as a field | an empty pack is AdHoc's cheapest signal; as a field its presence is all it says |
| `DoubleValue`, `FloatValue`, `BoolValue`, `StringValue`, `BytesValue` | `double?`, `float?`, `bool?`, `string?`, `Binary[,,]?` | a wrapper exists to make a scalar optional |
| `Int32Value`, `Int64Value`, `UInt32Value`, `UInt64Value` | `[X] int?`, `[X] long?`, `[A] uint?`, `[A] ulong?` | |
| `google.protobuf.Any` | `Binary[,,]` | a serialized message and the URL of its type; noted at the field |
| `google.protobuf.FieldMask` | `string[,,]` | |
| `google.protobuf.Struct`, `Value`, `ListValue` | packs written out as `struct.proto` declares them | a JSON value as a pack of optional alternatives |
| a message with no fields, as the type of a field | `bool` | noted at the field |

They are recognised by what a name resolves to, not by how it is written: a local `message Value` is that
message, `google.protobuf.Value` is the well-known one. The files of `google/protobuf` that declare them need
not be on disk: `protoc` carries them inside itself, and so does the converter.

### Services

| Protobuf | AdHoc | Notes |
|:--|:--|:--|
| `service` | an interface in the connection | groups the methods as the document does |
| `rpc M(Req) returns (Res)` | `(L____________, Res) M(Req req);` | Left = Client calls, Right = Server answers |
| `rpc M(Empty) returns (Res)` | `(L____________, Res) M(NoArg _);` | |
| `rpc M(Req) returns (Empty)` | a one-way call: `[l____________<Req>] struct M { }` in the actor `OneWay` of the service | nothing comes back, so nothing is waited for |
| `rpc M(Req) returns (stream Res)` | `interface M : Actor`: the items go by `[____________r<Res>]` while the actor stays in its state, `EndOfStream` ends the call | |
| `rpc M(stream Req) returns (Res)` | `interface M : Actor`: the items go by `[l____________<Req>]`, `EndOfStream` passes the turn, the reply ends the call | |
| `rpc M(stream Req) returns (stream Res)` | `interface M : Actor` with two states: both send, then the caller says `EndOfStream` and the answerer finishes | |
| the path gRPC knows a method by, `/package.Service/Method` | where the method is declared: the namespace, the interface of the service, the method | no attribute repeats it; a name that had to change says in its doc what the `.proto` calls it |
| a message of the file that no method carries and no field is typed with | the state `Exchange`: `[_____lr_____<(A, B)>]` | it travels on its own, either way; without services these are the root messages of the file |

A call that answers with `Empty` is taken for a notification. If the caller has to know that the call was done,
write it as a method that answers with the empty pack: `(L____________, NoArg) M(Req req);`.

### Options

| Protobuf | AdHoc | Notes |
|:--|:--|:--|
| `deprecated = true` | `[Obsolete]` | on a value of an enum: a line of its doc |
| `default = …` (proto2) | `[Default(…)]` | metadata: AdHoc sends what the field holds |
| an option the authors declared, `(google.api.http) = { get: "/v1/items" }` | `[Option(@"(google.api.http)", @"{ get: ""/v1/items"" }")]` | on the file, a message, a field, a oneof, a method; on a service and on a value of an enum: a line of the doc |
| `java_package`, `go_package`, `optimize_for`, `packed`, `json_name`, `ctype`, `jstype`, `lazy`, `features.*` … | nothing | they steer other code generators or protobuf's own wire |

## Names

A name comes from the document and cannot always stay as it is. A description cannot hold:

- a keyword of any target language as a name - handled the way the agent does it (`type` → `Type`);
- a field and a nested type with one name, a member named like its pack;
- sibling types that differ only by case (`Client` / `client`): their generated files collide on Windows;
- a type with the name of a type of an enclosing scope: inside, the name would mean the nested one (a nested
  `Status` next to a top-level `Status`);
- a type called like something the description itself refers to (`File`, `Set`, `Duration`, `DateTime`);
- a type of another package named like a type of the file: inside its container the name would mean the nested one;
- a method called like its service (`service Watch { rpc Watch … }`).

Names are settled in one pass, top-down, when every file is read. **Of two names in conflict the deeper one
changes**, so the outer, more visible name keeps the spelling of the document. **It changes the way the agent
renames a keyword: its lowercase letters go upper case one after another until the conflict is gone**
(`Status` → `STatus`). Only names that differ by nothing but case cannot be told apart that way and get a number
(`client2`). References are resolved before that pass and kept as objects, so a rename reaches every use.

Three more rules, each for a name that is not the document's own:

- a segment of the package that is a keyword of C# starts with a capital in the namespace (`event` → `Event`);
- the pack of a oneof that wants a name a nested message has says what it is: `ChoiceOneof`;
- what the converter declares itself yields to the document: a file with a message `Client` gets the host
  `ClientHost`, one with a message `NoArg` gets `NoArgs`.

A reference is written as short as C# resolves it: by the simple name from inside the message a type is nested
in, by its path (`Outer.Inner`) from elsewhere.

## Files and imports

One description is written per `.proto` file. It holds what the file declares and, of the files it imports,
the types its own declarations are typed with - each with the top-level message it is nested in, and whatever
that one needs in turn. A type of the same package stands beside the file's own, a type of another package in
containers named after that package. So every description stands alone, and a type shared by two files is in both.

An import is looked for, in this order: in the import roots given with `-I`; in the folders above the importing
file, nearest first (one of them is the root the import was written from); by the end of its path under the
input folder, when exactly one file has it; among the well-known files of `google/protobuf`, which the
converter carries. An import that is not found is reported. If a type it declares is needed, the field is
carried as raw bytes with a note at the field; if it only declares options - `google/api/annotations.proto`
does - nothing is lost.

A file that declares no message and no service - enums only, or options only - has nothing to send and gets no
description: its enums are written out with the files that use them.

## What is dropped or approximated

- **Protobuf's wire format**: field numbers, `packed`, groups as a wire type, `message_set_wire_format`. None of
  it is needed: AdHoc lays out its own wire.
- **Options of code generators** (`java_package`, `go_package`, `csharp_namespace`, `optimize_for` …).
- **Negative and sub-millisecond durations**: `google.protobuf.Duration` has nanoseconds and a sign, an AdHoc
  `Duration` has neither.
- **`google.protobuf.Any`** is opaque bytes: which message it holds is told by its URL, at run time.
- **Declarations of options** (`extend google.protobuf.FieldOptions { … }`): the options applied are kept as
  `[Option]`, their declarations are not data.
- **That exactly one alternative of a oneof is set**: AdHoc leaves it to the code that fills the pack.
- **Whether a call answered with `Empty` was done**: it becomes a one-way call (see Services).
- **Early end of a two-way stream by the answerer**: in the generated actor the caller ends its stream first.
- **Collection bounds.** Protobuf declares none. `_DefaultMaxLengthOf` is set to 65 535 so nothing is cut
  silently at AdHoc's default of 255; tighten it and put `[D]` on the fields whose real bound is known.
- **String constants over 1000 characters** (a long value of an option) are cut at 1000 characters, and a
  comment above says so; the `.proto` has the whole text.
- **Services of imported files** are not written out: they belong to the description of their own file.
- **A comment separated from its declaration by a blank line** (a licence header, a banner of a section).

Everything dropped at a specific place leaves a comment at that place.

## Build & run

[.NET SDK 10](https://dotnet.microsoft.com/download) is required. There are no packages to restore.

```bash
./fetch-samples.sh        # download the upstream .proto files into samples/
./build.sh                # build into out/ and convert samples/ → AdHoc/
./validate.sh AdHoc       # AdHocAgent parse-only check of every generated file (nothing is uploaded)
```

Convert your own schemas:

```bash
dotnet out/Protobuf2AdHoc.dll path/to/service.proto                   # → ./AdHoc/service.cs
dotnet out/Protobuf2AdHoc.dll path/to/protos path/to/out              # every .proto under the folder
dotnet out/Protobuf2AdHoc.dll api/v1/service.proto out -I third_party # an extra root for imports
```

CLI contract: `<input file or folder> [output folder] [-I <import root>]...`; the output folder defaults to
`<cwd>/AdHoc`; one `.cs` per `.proto`, named after the file - two files of one name in different folders take
their folders into the name (`v1_service.cs`, `v2_service.cs`). Imports that are not found and types that
are not declared are printed. The exit code is 2 when an input could not be read or nothing was converted.

## Validating the output

`validate.sh` runs AdHocAgent with `ADHOC_PARSE_ONLY=1` on a copy of every `.cs` (the agent rewrites the file in
place) and prints `OK` when the agent exits with 0 and reports no ERR / WRN. It looks for `AdHocAgent.exe` on
`PATH`, then in a sibling build of the agent; set `AGENT=/path/to/AdHocAgent.exe` to point it elsewhere.

Result for the shipped samples - **37 of 37 descriptions OK**; three files have nothing to send and get no
description. Of the 123 methods of the samples 91 are RPC methods, 29 are streaming actors and 3 are one-way
calls.

| Sample | Syntax | Source lines | Generated lines | RPC methods | Streaming actors | One-way calls |
|:--|:--|--:|--:|--:|--:|--:|
| [`arrow/format/Flight.proto`](https://github.com/apache/arrow/blob/main/format/Flight.proto) | proto3 | 678 | 780 | 3 | 7 | 0 |
| [`cloudevents/cloudevents.proto`](https://github.com/cloudevents/spec/blob/main/cloudevents/formats/cloudevents.proto) | proto3 | 69 | 102 | 0 | 0 | 0 |
| [`containerd/api/services/containers/v1/containers.proto`](https://github.com/containerd/containerd/blob/main/api/services/containers/v1/containers.proto) | proto3 | 181 | 262 | 4 | 1 | 1 |
| [`containerd/api/services/events/v1/events.proto`](https://github.com/containerd/containerd/blob/main/api/services/events/v1/events.proto) | proto3 | 62 | 140 | 0 | 1 | 2 |
| [`containerd/api/types/event.proto`](https://github.com/containerd/containerd/blob/main/api/types/event.proto) | proto3 | 33 | 73 | 0 | 0 | 0 |
| [`containerd/api/types/fieldpath.proto`](https://github.com/containerd/containerd/blob/main/api/types/fieldpath.proto) | proto3 | 42 | options only | | | |
| [`etcd/api/authpb/auth.proto`](https://github.com/etcd-io/etcd/blob/main/api/authpb/auth.proto) | proto3 | 36 | 90 | 0 | 0 | 0 |
| [`etcd/api/etcdserverpb/rpc.proto`](https://github.com/etcd-io/etcd/blob/main/api/etcdserverpb/rpc.proto) | proto3 | 1 462 | 1 705 | 38 | 4 | 0 |
| [`etcd/api/mvccpb/kv.proto`](https://github.com/etcd-io/etcd/blob/main/api/mvccpb/kv.proto) | proto3 | 43 | 116 | 0 | 0 | 0 |
| [`etcd/api/versionpb/version.proto`](https://github.com/etcd-io/etcd/blob/main/api/versionpb/version.proto) | proto3 | 26 | options only | | | |
| [`googleapis/google/api/annotations.proto`](https://github.com/googleapis/googleapis/blob/master/google/api/annotations.proto) | proto3 | 31 | options only | | | |
| [`googleapis/google/api/http.proto`](https://github.com/googleapis/googleapis/blob/master/google/api/http.proto) | proto3 | 370 | 427 | 0 | 0 | 0 |
| [`googleapis/google/bytestream/bytestream.proto`](https://github.com/googleapis/googleapis/blob/master/google/bytestream/bytestream.proto) | proto3 | 178 | 265 | 1 | 2 | 0 |
| [`googleapis/google/rpc/error_details.proto`](https://github.com/googleapis/googleapis/blob/master/google/rpc/error_details.proto) | proto3 | 363 | 479 | 0 | 0 | 0 |
| [`googleapis/google/rpc/status.proto`](https://github.com/googleapis/googleapis/blob/master/google/rpc/status.proto) | proto3 | 48 | 89 | 0 | 0 | 0 |
| [`googleapis/google/type/date.proto`](https://github.com/googleapis/googleapis/blob/master/google/type/date.proto) | proto3 | 54 | 94 | 0 | 0 | 0 |
| [`googleapis/google/type/latlng.proto`](https://github.com/googleapis/googleapis/blob/master/google/type/latlng.proto) | proto3 | 36 | 70 | 0 | 0 | 0 |
| [`googleapis/google/type/money.proto`](https://github.com/googleapis/googleapis/blob/master/google/type/money.proto) | proto3 | 41 | 81 | 0 | 0 | 0 |
| [`grpc/health.proto`](https://github.com/grpc/grpc-proto/blob/master/grpc/health/v1/health.proto) | proto3 | 92 | 142 | 2 | 1 | 0 |
| [`grpc/helloworld.proto`](https://github.com/grpc/grpc/blob/master/examples/protos/helloworld.proto) | proto3 | 42 | 96 | 1 | 2 | 0 |
| [`grpc/reflection.proto`](https://github.com/grpc/grpc-proto/blob/master/grpc/reflection/v1/reflection.proto) | proto3 | 147 | 226 | 0 | 1 | 0 |
| [`grpc/route_guide.proto`](https://github.com/grpc/grpc/blob/master/examples/protos/route_guide.proto) | proto3 | 111 | 193 | 1 | 3 | 0 |
| [`kubernetes/cri-api/runtime/v1/api.proto`](https://github.com/kubernetes/cri-api/blob/master/pkg/apis/runtime/v1/api.proto) | proto3 | 2 398 | 3 286 | 36 | 7 | 0 |
| [`opentelemetry/…/collector/logs/v1/logs_service.proto`](https://github.com/open-telemetry/opentelemetry-proto/blob/main/opentelemetry/proto/collector/logs/v1/logs_service.proto) | proto3 | 77 | 546 | 1 | 0 | 0 |
| [`opentelemetry/…/collector/metrics/v1/metrics_service.proto`](https://github.com/open-telemetry/opentelemetry-proto/blob/main/opentelemetry/proto/collector/metrics/v1/metrics_service.proto) | proto3 | 77 | 1 155 | 1 | 0 | 0 |
| [`opentelemetry/…/collector/trace/v1/trace_service.proto`](https://github.com/open-telemetry/opentelemetry-proto/blob/main/opentelemetry/proto/collector/trace/v1/trace_service.proto) | proto3 | 77 | 711 | 1 | 0 | 0 |
| [`opentelemetry/…/common/v1/common.proto`](https://github.com/open-telemetry/opentelemetry-proto/blob/main/opentelemetry/proto/common/v1/common.proto) | proto3 | 159 | 243 | 0 | 0 | 0 |
| [`opentelemetry/…/logs/v1/logs.proto`](https://github.com/open-telemetry/opentelemetry-proto/blob/main/opentelemetry/proto/logs/v1/logs.proto) | proto3 | 228 | 523 | 0 | 0 | 0 |
| [`opentelemetry/…/metrics/v1/metrics.proto`](https://github.com/open-telemetry/opentelemetry-proto/blob/main/opentelemetry/proto/metrics/v1/metrics.proto) | proto3 | 737 | 1 153 | 0 | 0 | 0 |
| [`opentelemetry/…/resource/v1/resource.proto`](https://github.com/open-telemetry/opentelemetry-proto/blob/main/opentelemetry/proto/resource/v1/resource.proto) | proto3 | 47 | 248 | 0 | 0 | 0 |
| [`opentelemetry/…/trace/v1/trace.proto`](https://github.com/open-telemetry/opentelemetry-proto/blob/main/opentelemetry/proto/trace/v1/trace.proto) | proto3 | 368 | 707 | 0 | 0 | 0 |
| [`protobuf/addressbook.proto`](https://github.com/protocolbuffers/protobuf/blob/main/examples/addressbook.proto) | proto3 | 56 | 83 | 0 | 0 | 0 |
| [`protobuf/google/protobuf/descriptor.proto`](https://github.com/protocolbuffers/protobuf/blob/main/src/google/protobuf/descriptor.proto) | proto2 | 1 558 | 1 597 | 0 | 0 | 0 |
| [`protobuf/google/protobuf/map_unittest.proto`](https://github.com/protocolbuffers/protobuf/blob/main/src/google/protobuf/map_unittest.proto) | proto3 | 123 | 466 | 0 | 0 | 0 |
| [`protobuf/google/protobuf/unittest.proto`](https://github.com/protocolbuffers/protobuf/blob/main/src/google/protobuf/unittest.proto) | edition 2023 | 2 731 | 2 432 | 2 | 0 | 0 |
| [`protobuf/google/protobuf/unittest_import.proto`](https://github.com/protocolbuffers/protobuf/blob/main/src/google/protobuf/unittest_import.proto) | proto2 | 49 | 72 | 0 | 0 | 0 |
| [`protobuf/google/protobuf/unittest_import_public.proto`](https://github.com/protocolbuffers/protobuf/blob/main/src/google/protobuf/unittest_import_public.proto) | proto2 | 18 | 59 | 0 | 0 | 0 |
| [`protobuf/google/protobuf/unittest_mset.proto`](https://github.com/protocolbuffers/protobuf/blob/main/src/google/protobuf/unittest_mset.proto) | proto2 | 82 | 123 | 0 | 0 | 0 |
| [`protobuf/google/protobuf/unittest_mset_wire_format.proto`](https://github.com/protocolbuffers/protobuf/blob/main/src/google/protobuf/unittest_mset_wire_format.proto) | proto2 | 38 | 68 | 0 | 0 | 0 |
| [`protobuf/google/protobuf/unittest_proto3.proto`](https://github.com/protocolbuffers/protobuf/blob/main/src/google/protobuf/unittest_proto3.proto) | proto3 | 283 | 340 | 0 | 0 | 0 |

`trace_service.proto` is 77 lines and its description 711: the one method it declares carries a tree of
messages from three imported files, and they are written out with it.

`tests/` holds synthetic schemas, all **OK**; run them after changing the converter:

```bash
dotnet out/Protobuf2AdHoc.dll tests tests/AdHoc && ./validate.sh tests/AdHoc
```

| Schema | What it is for |
|:--|:--|
| [`coverage.proto`](tests/coverage.proto) | every construct the converter maps, once: all scalar types, `repeated`, maps, a oneof, the well-known types, options, the four kinds of a method, a message no method carries |
| [`edge-cases.proto`](tests/edge-cases.proto) | every name conflict the converter settles and every shape it has to rewrite: messages called `File`, `Client`, `Server`, `NoArg`, `Duration`; `Client` next to `client`; a field named like its message and like its type; a nested `Status` next to a top-level one and next to an imported one; groups; extensions, one named like a field of its target; an enum with one value; `allow_alias`; a map keyed by a boolean; a default with bytes that are not text; an import that is not found; a oneof that wants the name of a nested message; a service named like a message; a method named like its service; a stream of empty messages |
| [`editions.proto`](tests/editions.proto) | the editions syntax: presence said by a feature, names reserved without quotes |
| [`imported/money.proto`](tests/imported/money.proto) | what `edge-cases.proto` imports: another package with a type of the same name |
