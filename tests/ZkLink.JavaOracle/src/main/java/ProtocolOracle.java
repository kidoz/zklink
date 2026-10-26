import java.io.*;
import java.nio.file.*;
import java.util.*;
import java.util.function.Supplier;
import org.apache.jute.BinaryInputArchive;
import org.apache.jute.BinaryOutputArchive;
import org.apache.jute.Record;
import org.apache.zookeeper.*;
import org.apache.zookeeper.data.*;
import org.apache.zookeeper.proto.*;
import org.apache.zookeeper.common.PathUtils;

// Original differential-test utility using the pinned Apache serializers, including real multi.
public final class ProtocolOracle {
    private static final Stat STAT = new Stat(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11);
    private static final List<ACL> ACLS = List.of(new ACL(31, new Id("world", "anyone")));
    private static final byte[] DATA = {0, (byte)255, 42};
    private static final byte[] PASSWORD = new byte[16]; // Synthetic fixture, never live credentials.
    private record Body(Record value, Supplier<Record> fresh) {}
    private record Fixture(int xid, int opcode, Body request, Body response) {}

    private static Map<String, Fixture> fixtures() {
        var map = new LinkedHashMap<String, Fixture>();
        map.put("create", new Fixture(1, ZooDefs.OpCode.create, new Body(new CreateRequest("/node", DATA, ACLS, CreateMode.PERSISTENT.toFlag()), CreateRequest::new), new Body(new CreateResponse("/node"), CreateResponse::new)));
        map.put("delete", new Fixture(1, ZooDefs.OpCode.delete, new Body(new DeleteRequest("/node", -1), DeleteRequest::new), null));
        map.put("exists", new Fixture(1, ZooDefs.OpCode.exists, new Body(new ExistsRequest("/node", true), ExistsRequest::new), new Body(new ExistsResponse(STAT), ExistsResponse::new)));
        map.put("get-data", new Fixture(1, ZooDefs.OpCode.getData, new Body(new GetDataRequest("/node", false), GetDataRequest::new), new Body(new GetDataResponse(DATA, STAT), GetDataResponse::new)));
        map.put("get-null-data", new Fixture(1, ZooDefs.OpCode.getData, new Body(new GetDataRequest("/node", false), GetDataRequest::new), new Body(new GetDataResponse(null, STAT), GetDataResponse::new)));
        map.put("get-empty-data", new Fixture(1, ZooDefs.OpCode.getData, new Body(new GetDataRequest("/node", false), GetDataRequest::new), new Body(new GetDataResponse(new byte[0], STAT), GetDataResponse::new)));
        map.put("set-data", new Fixture(1, ZooDefs.OpCode.setData, new Body(new SetDataRequest("/node", DATA, Integer.MIN_VALUE), SetDataRequest::new), new Body(new SetDataResponse(STAT), SetDataResponse::new)));
        map.put("get-acl", new Fixture(1, ZooDefs.OpCode.getACL, new Body(new GetACLRequest("/node"), GetACLRequest::new), new Body(new GetACLResponse(ACLS, STAT), GetACLResponse::new)));
        map.put("set-acl", new Fixture(1, ZooDefs.OpCode.setACL, new Body(new SetACLRequest("/node", ACLS, -1), SetACLRequest::new), new Body(new SetACLResponse(STAT), SetACLResponse::new)));
        map.put("get-children", new Fixture(1, ZooDefs.OpCode.getChildren, new Body(new GetChildrenRequest("/node", false), GetChildrenRequest::new), new Body(new GetChildrenResponse(List.of("a", "\u0416")), GetChildrenResponse::new)));
        map.put("sync", new Fixture(1, ZooDefs.OpCode.sync, new Body(new SyncRequest("/node"), SyncRequest::new), new Body(new SyncResponse("/node"), SyncResponse::new)));
        map.put("ping", new Fixture(ClientCnxn.PING_XID, ZooDefs.OpCode.ping, null, null));
        map.put("get-children-stat", new Fixture(1, ZooDefs.OpCode.getChildren2, new Body(new GetChildren2Request("/node", true), GetChildren2Request::new), new Body(new GetChildren2Response(List.of("a", "\u0416"), STAT), GetChildren2Response::new)));
        map.put("check", new Fixture(1, ZooDefs.OpCode.check, new Body(new CheckVersionRequest("/node", -1), CheckVersionRequest::new), null));
        map.put("create-stat", new Fixture(1, ZooDefs.OpCode.create2, new Body(new CreateRequest("/node", DATA, ACLS, CreateMode.PERSISTENT.toFlag()), CreateRequest::new), new Body(new Create2Response("/node", STAT), Create2Response::new)));
        map.put("create-ephemeral", new Fixture(1, ZooDefs.OpCode.create2, new Body(new CreateRequest("/node", DATA, ACLS, CreateMode.EPHEMERAL.toFlag()), CreateRequest::new), new Body(new Create2Response("/node", STAT), Create2Response::new)));
        map.put("create-sequential", new Fixture(1, ZooDefs.OpCode.create2, new Body(new CreateRequest("/node", DATA, ACLS, CreateMode.PERSISTENT_SEQUENTIAL.toFlag()), CreateRequest::new), new Body(new Create2Response("/node0000000001", STAT), Create2Response::new)));
        map.put("create-ephemeral-sequential", new Fixture(1, ZooDefs.OpCode.create2, new Body(new CreateRequest("/node", DATA, ACLS, CreateMode.EPHEMERAL_SEQUENTIAL.toFlag()), CreateRequest::new), new Body(new Create2Response("/node0000000001", STAT), Create2Response::new)));
        map.put("create-sequential-prefix", new Fixture(1, ZooDefs.OpCode.create2, new Body(new CreateRequest("/node/", DATA, ACLS, CreateMode.PERSISTENT_SEQUENTIAL.toFlag()), CreateRequest::new), new Body(new Create2Response("/node/0000000001", STAT), Create2Response::new)));
        map.put("check-watches", new Fixture(1, ZooDefs.OpCode.checkWatches, new Body(new CheckWatchesRequest("/node", 3), CheckWatchesRequest::new), null));
        map.put("remove-watches", new Fixture(1, ZooDefs.OpCode.removeWatches, new Body(new RemoveWatchesRequest("/node", 4), RemoveWatchesRequest::new), null));
        map.put("create-container", new Fixture(1, ZooDefs.OpCode.createContainer, new Body(new CreateRequest("/node", DATA, ACLS, CreateMode.CONTAINER.toFlag()), CreateRequest::new), new Body(new Create2Response("/node", STAT), Create2Response::new)));
        map.put("create-ttl", new Fixture(1, ZooDefs.OpCode.createTTL, new Body(new CreateTTLRequest("/node", DATA, ACLS, CreateMode.PERSISTENT_WITH_TTL.toFlag(), 1234), CreateTTLRequest::new), new Body(new Create2Response("/node", STAT), Create2Response::new)));
        map.put("create-ttl-sequential", new Fixture(1, ZooDefs.OpCode.createTTL, new Body(new CreateTTLRequest("/node", DATA, ACLS, CreateMode.PERSISTENT_SEQUENTIAL_WITH_TTL.toFlag(), 1099511627775L), CreateTTLRequest::new), new Body(new Create2Response("/node0000000001", STAT), Create2Response::new)));
        map.put("auth", new Fixture(ClientCnxn.AUTHPACKET_XID, ZooDefs.OpCode.auth, new Body(new AuthPacket(0, "digest", DATA), AuthPacket::new), null));
        map.put("restore", new Fixture(ClientCnxn.SET_WATCHES_XID, ZooDefs.OpCode.setWatches, new Body(new SetWatches(12, List.of("/data"), List.of("/exists"), List.of("/children")), SetWatches::new), null));
        map.put("get-ephemerals", new Fixture(1, ZooDefs.OpCode.getEphemerals, new Body(new GetEphemeralsRequest("/node"), GetEphemeralsRequest::new), new Body(new GetEphemeralsResponse(List.of("/node", "/node2")), GetEphemeralsResponse::new)));
        map.put("count-children", new Fixture(1, ZooDefs.OpCode.getAllChildrenNumber, new Body(new GetAllChildrenNumberRequest("/node"), GetAllChildrenNumberRequest::new), new Body(new GetAllChildrenNumberResponse(123), GetAllChildrenNumberResponse::new)));
        map.put("restore-persistent", new Fixture(ClientCnxn.SET_WATCHES_XID, ZooDefs.OpCode.setWatches2, new Body(new SetWatches2(12, List.of("/data"), List.of("/exists"), List.of("/children"), List.of("/persistent"), List.of("/recursive")), SetWatches2::new), null));
        map.put("add-watch", new Fixture(1, ZooDefs.OpCode.addWatch, new Body(new AddWatchRequest("/node", 1), AddWatchRequest::new), new Body(new ErrorResponse(KeeperException.Code.OK.intValue()), ErrorResponse::new)));
        map.put("who-am-i", new Fixture(1, ZooDefs.OpCode.whoAmI, null, new Body(new WhoAmIResponse(List.of(new ClientInfo("digest", "fixture-user"))), WhoAmIResponse::new)));
        map.put("close", new Fixture(1, ZooDefs.OpCode.closeSession, null, null));

        var writes = new MultiOperationRecord(List.of(
            Op.create("/node", DATA, ACLS, CreateMode.PERSISTENT.toFlag()),
            Op.create("/node", DATA, CreateOptions.newBuilder(ACLS, CreateMode.PERSISTENT).build()),
            Op.create("/node", DATA, ACLS, CreateMode.CONTAINER.toFlag()),
            Op.create("/node", DATA, ACLS, CreateMode.PERSISTENT_WITH_TTL.toFlag(), 1234),
            Op.setData("/node", DATA, -1), Op.delete("/node", -1), Op.check("/node", -1)));
        var writeResults = new MultiResponse();
        writeResults.add(new OpResult.CreateResult("/node"));
        for (int i = 0; i < 3; i++) writeResults.add(new OpResult.CreateResult("/node", STAT));
        writeResults.add(new OpResult.SetDataResult(STAT));
        writeResults.add(new OpResult.DeleteResult());
        writeResults.add(new OpResult.CheckResult());
        map.put("multi-write", new Fixture(1, ZooDefs.OpCode.multi, new Body(writes, MultiOperationRecord::new), new Body(writeResults, MultiResponse::new)));
        var failed = new MultiResponse();
        failed.add(new OpResult.ErrorResult(0));
        failed.add(new OpResult.ErrorResult(KeeperException.Code.BADVERSION.intValue()));
        for (int i = 2; i < 7; i++) failed.add(new OpResult.ErrorResult(KeeperException.Code.RUNTIMEINCONSISTENCY.intValue()));
        map.put("multi-failed", new Fixture(1, ZooDefs.OpCode.multi, new Body(writes, MultiOperationRecord::new), new Body(failed, MultiResponse::new)));
        var reads = new MultiOperationRecord(List.of(Op.getData("/node"), Op.getChildren("/node")));
        var readResults = new MultiResponse();
        readResults.add(new OpResult.GetDataResult(DATA, STAT));
        readResults.add(new OpResult.GetChildrenResult(List.of("a", "\u0416")));
        map.put("multi-read", new Fixture(1, ZooDefs.OpCode.multiRead, new Body(reads, MultiOperationRecord::new), new Body(readResults, MultiResponse::new)));
        var readErrors = new MultiResponse();
        readErrors.add(new OpResult.ErrorResult(KeeperException.Code.NONODE.intValue()));
        readErrors.add(new OpResult.GetChildrenResult(List.of("a", "\u0416")));
        map.put("multi-read-error", new Fixture(1, ZooDefs.OpCode.multiRead, new Body(reads, MultiOperationRecord::new), new Body(readErrors, MultiResponse::new)));
        return map;
    }

    private static byte[] packet(Fixture fixture, boolean request, int error) throws IOException {
        var bytes = new ByteArrayOutputStream();
        var out = BinaryOutputArchive.getArchive(bytes);
        if (request) new RequestHeader(fixture.xid, fixture.opcode).serialize(out, "header");
        else new ReplyHeader(fixture.xid, 101, error).serialize(out, "header");
        Body body = request ? fixture.request : fixture.response;
        if (body != null && (request || error == 0)) body.value.serialize(out, "body");
        return bytes.toByteArray();
    }

    private static void verify(byte[] bytes, Fixture fixture) throws IOException {
        var stream = new ByteArrayInputStream(bytes);
        var in = BinaryInputArchive.getArchive(stream);
        var header = new RequestHeader(); header.deserialize(in, "header");
        if (header.getXid() != fixture.xid || header.getType() != fixture.opcode) throw new IOException("Request header mismatch");
        if (fixture.request != null) {
            Record actual = fixture.request.fresh.get(); actual.deserialize(in, "body");
            if (!actual.equals(fixture.request.value)) throw new IOException("Request semantic mismatch");
        }
        if (stream.available() != 0) throw new IOException("Trailing request bytes");
    }

    private static byte[] record(Record value) throws IOException {
        var stream = new ByteArrayOutputStream(); value.serialize(BinaryOutputArchive.getArchive(stream), "fixture");
        return stream.toByteArray();
    }

    private static void save(Path directory, String name, byte[] bytes) throws IOException {
        Files.writeString(directory.resolve(name + ".hex"), HexFormat.of().formatHex(bytes) + "\n");
    }

    public static void main(String[] args) throws IOException {
        Path directory = Path.of(args[1]);
        if (args[0].equals("generate")) {
            Files.createDirectories(directory);
            for (var entry : fixtures().entrySet()) {
                byte[] request = packet(entry.getValue(), true, 0);
                verify(request, entry.getValue());
                save(directory, entry.getKey() + "-request", request);
                save(directory, entry.getKey() + "-reply", packet(entry.getValue(), false, 0));
                save(directory, entry.getKey() + "-error", packet(entry.getValue(), false, KeeperException.Code.NONODE.intValue()));
            }
            save(directory, "connect-new", record(new ConnectRequest(0, 0, 30000, 0, PASSWORD, false)));
            save(directory, "connect-resume", record(new ConnectRequest(0, 12, 30000, 123, PASSWORD, false)));
            save(directory, "connect-reply", record(new ConnectResponse(0, 20000, 123, PASSWORD, false)));
            save(directory, "connect-expired", record(new ConnectResponse(0, 0, 0, PASSWORD, false)));
            var stream = new ByteArrayOutputStream();
            var out = BinaryOutputArchive.getArchive(stream);
            new ReplyHeader(ClientCnxn.NOTIFICATION_XID, 102, 0).serialize(out, "header");
            new WatcherEvent(Watcher.Event.EventType.NodeDataChanged.getIntValue(), Watcher.Event.KeeperState.SyncConnected.getIntValue(), "/node").serialize(out, "event");
            save(directory, "notification", stream.toByteArray());
            var validCodeUnits = new byte[8192];
            for (int code = 0; code <= 0xffff; code++) {
                try {
                    PathUtils.validatePath("/a" + (char)code + "b");
                    validCodeUnits[code / 8] |= (byte)(1 << (code % 8));
                } catch (IllegalArgumentException expected) { }
            }
            save(directory, "path-code-units", validCodeUnits);
            var manifest = new StringBuilder("{\n  \"sourceCommit\": \"a355171b081b5b60749db8f19cca1528b0df936f\",\n  \"artifactVersion\": \"3.9.6\",\n  \"cases\": [\n");
            int number = 0;
            for (var entry : fixtures().entrySet()) {
                if (number++ != 0) manifest.append(",\n");
                Fixture fixture = entry.getValue();
                manifest.append("    {\"name\": \"").append(entry.getKey()).append("\", \"opcode\": ").append(fixture.opcode)
                    .append(", \"xid\": ").append(fixture.xid).append(", \"requestRecord\": \"")
                    .append(fixture.request == null ? "none" : fixture.request.value.getClass().getSimpleName())
                    .append("\", \"responseRecord\": \"")
                    .append(fixture.response == null ? "none" : fixture.response.value.getClass().getSimpleName()).append("\"}");
            }
            manifest.append("\n  ]\n}\n");
            Files.writeString(directory.resolve("manifest.json"), manifest);
        } else if (args[0].equals("verify")) {
            for (var entry : fixtures().entrySet()) verify(HexFormat.of().parseHex(Files.readString(directory.resolve(entry.getKey() + "-request.hex")).trim()), entry.getValue());
            for (String name : List.of("connect-new", "connect-resume")) {
                byte[] bytes = HexFormat.of().parseHex(Files.readString(directory.resolve(name + ".hex")).trim());
                var in = new ByteArrayInputStream(bytes); var actual = new ConnectRequest(); actual.deserialize(BinaryInputArchive.getArchive(in), "fixture");
                var expected = new ConnectRequest(0, name.equals("connect-new") ? 0 : 12, 30000, name.equals("connect-new") ? 0 : 123, PASSWORD, false);
                if (!actual.equals(expected) || in.available() != 0) throw new IOException("Connect request mismatch");
            }
        } else throw new IllegalArgumentException("Expected generate or verify");
    }
}
