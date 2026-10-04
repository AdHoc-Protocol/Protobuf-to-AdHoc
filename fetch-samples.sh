#!/usr/bin/env bash
# Downloads real .proto files from their upstream repositories into samples/, keeping the upstream layout
# so that the imports resolve.
#   ./fetch-samples.sh
set -u
cd "$(dirname "$0")"
PB="https://raw.githubusercontent.com/protocolbuffers/protobuf/main"
GRPC="https://raw.githubusercontent.com/grpc/grpc/master/examples/protos"
HEALTH="https://raw.githubusercontent.com/grpc/grpc-proto/master/grpc/health/v1"
CONTAINERD="https://raw.githubusercontent.com/containerd/containerd/main/api"
GAPIS="https://raw.githubusercontent.com/googleapis/googleapis/master"
ETCD="https://raw.githubusercontent.com/etcd-io/etcd/main/api"
OTEL="https://raw.githubusercontent.com/open-telemetry/opentelemetry-proto/main"
CRI="https://raw.githubusercontent.com/kubernetes/cri-api/master/pkg/apis/runtime/v1"
ARROW="https://raw.githubusercontent.com/apache/arrow/main/format"
GRPCPROTO="https://raw.githubusercontent.com/grpc/grpc-proto/master/grpc"
CLOUDEVENTS="https://raw.githubusercontent.com/cloudevents/spec/main/cloudevents/formats"
# <path in samples/> <url>
FILES="
grpc/helloworld.proto                                   $GRPC/helloworld.proto
grpc/route_guide.proto                                  $GRPC/route_guide.proto
grpc/health.proto                                       $HEALTH/health.proto
grpc/reflection.proto                                   $GRPCPROTO/reflection/v1/reflection.proto
protobuf/addressbook.proto                              $PB/examples/addressbook.proto
protobuf/google/protobuf/descriptor.proto               $PB/src/google/protobuf/descriptor.proto
protobuf/google/protobuf/unittest.proto                 $PB/src/google/protobuf/unittest.proto
protobuf/google/protobuf/unittest_import.proto          $PB/src/google/protobuf/unittest_import.proto
protobuf/google/protobuf/unittest_import_public.proto   $PB/src/google/protobuf/unittest_import_public.proto
protobuf/google/protobuf/unittest_proto3.proto          $PB/src/google/protobuf/unittest_proto3.proto
protobuf/google/protobuf/map_unittest.proto             $PB/src/google/protobuf/map_unittest.proto
protobuf/google/protobuf/unittest_mset.proto            $PB/src/google/protobuf/unittest_mset.proto
protobuf/google/protobuf/unittest_mset_wire_format.proto $PB/src/google/protobuf/unittest_mset_wire_format.proto
googleapis/google/api/annotations.proto                 $GAPIS/google/api/annotations.proto
googleapis/google/api/http.proto                        $GAPIS/google/api/http.proto
googleapis/google/bytestream/bytestream.proto           $GAPIS/google/bytestream/bytestream.proto
googleapis/google/rpc/error_details.proto               $GAPIS/google/rpc/error_details.proto
googleapis/google/rpc/status.proto                      $GAPIS/google/rpc/status.proto
googleapis/google/type/date.proto                       $GAPIS/google/type/date.proto
googleapis/google/type/latlng.proto                     $GAPIS/google/type/latlng.proto
googleapis/google/type/money.proto                      $GAPIS/google/type/money.proto
etcd/api/etcdserverpb/rpc.proto                         $ETCD/etcdserverpb/rpc.proto
etcd/api/mvccpb/kv.proto                                $ETCD/mvccpb/kv.proto
etcd/api/authpb/auth.proto                              $ETCD/authpb/auth.proto
etcd/api/versionpb/version.proto                        $ETCD/versionpb/version.proto
containerd/api/services/containers/v1/containers.proto  $CONTAINERD/services/containers/v1/containers.proto
containerd/api/services/events/v1/events.proto          $CONTAINERD/services/events/v1/events.proto
containerd/api/types/event.proto                        $CONTAINERD/types/event.proto
containerd/api/types/fieldpath.proto                    $CONTAINERD/types/fieldpath.proto
opentelemetry/opentelemetry/proto/common/v1/common.proto                    $OTEL/opentelemetry/proto/common/v1/common.proto
opentelemetry/opentelemetry/proto/resource/v1/resource.proto                $OTEL/opentelemetry/proto/resource/v1/resource.proto
opentelemetry/opentelemetry/proto/trace/v1/trace.proto                      $OTEL/opentelemetry/proto/trace/v1/trace.proto
opentelemetry/opentelemetry/proto/collector/trace/v1/trace_service.proto    $OTEL/opentelemetry/proto/collector/trace/v1/trace_service.proto
opentelemetry/opentelemetry/proto/metrics/v1/metrics.proto                  $OTEL/opentelemetry/proto/metrics/v1/metrics.proto
opentelemetry/opentelemetry/proto/logs/v1/logs.proto                        $OTEL/opentelemetry/proto/logs/v1/logs.proto
opentelemetry/opentelemetry/proto/collector/metrics/v1/metrics_service.proto $OTEL/opentelemetry/proto/collector/metrics/v1/metrics_service.proto
opentelemetry/opentelemetry/proto/collector/logs/v1/logs_service.proto      $OTEL/opentelemetry/proto/collector/logs/v1/logs_service.proto
kubernetes/cri-api/runtime/v1/api.proto                 $CRI/api.proto
arrow/format/Flight.proto                               $ARROW/Flight.proto
cloudevents/cloudevents.proto                           $CLOUDEVENTS/cloudevents.proto
"
# The page of a file in its repository, for a reader: raw.githubusercontent.com gives the bare text.
page() {
    case "$1" in
        https://raw.githubusercontent.com/*)
            local p="${1#https://raw.githubusercontent.com/}"
            local owner="${p%%/*}"; p="${p#*/}"
            local repo="${p%%/*}"; p="${p#*/}"
            echo "https://github.com/$owner/$repo/blob/$p" ;;
        *) echo "$1" ;;
    esac
}
mkdir -p samples
{
    echo "# Where every sample comes from: <path in samples/> <page of the original>. Written by fetch-samples.sh;"
    echo "# the converter links these pages in the headers of the descriptions."
    while read -r n url; do
        [ -n "$n" ] && printf '%-56s %s\n' "$n" "$(page "$url")"
    done <<< "$FILES"
} > samples/sources.txt
status=0
while read -r n url; do
    [ -z "$n" ] && continue
    mkdir -p "samples/$(dirname "$n")"
    if curl -sSfL -A 'Mozilla/5.0' -o "samples/$n" "$url"; then echo "ok      $n"; else echo "FAILED  $n  $url"; status=1; fi
done <<< "$FILES"
exit $status
