import java.io.*;
import java.nio.file.*;
import java.util.*;
import org.apache.jute.*;
import org.apache.zookeeper.data.Stat;
import org.apache.zookeeper.proto.*;

// Original fixture utility using Apache's published serializers, not a second codec.
public final class JuteOracle {
    private static final int[] INTS = {Integer.MIN_VALUE, -1, 0, 1, Integer.MAX_VALUE};
    private static final long[] LONGS = {Long.MIN_VALUE, -1, 0, 1, Long.MAX_VALUE};
    private static final List<String> STRINGS = Arrays.asList(null, "", "ZooKeeper", "\u0416\u4e2d\u00e9");
    private static final Stat STAT = new Stat(Long.MIN_VALUE, Long.MAX_VALUE, -1, 0,
        Integer.MIN_VALUE, -1, Integer.MAX_VALUE, 0x0102030405060708L, 123, 456, -2);

    private static byte[] encode() throws IOException {
        var bytes = new ByteArrayOutputStream();
        var out = BinaryOutputArchive.getArchive(bytes);
        for (int value : INTS) out.writeInt(value, "int");
        for (long value : LONGS) out.writeLong(value, "long");
        out.writeBool(false, "bool");
        out.writeBool(true, "bool");
        for (String value : STRINGS) out.writeString(value, "string");
        out.writeBuffer(null, "buffer");
        out.writeBuffer(new byte[0], "buffer");
        out.writeBuffer(new byte[] {0, (byte)255, (byte)128}, "buffer");
        out.startVector(STRINGS, "vector");
        for (String value : STRINGS) out.writeString(value, "item");
        out.endVector(STRINGS, "vector");
        out.startVector(null, "nullVector");
        out.endVector(null, "nullVector");
        STAT.serialize(out, "stat");
        new RequestHeader(1, 4).serialize(out, "request");
        new ReplyHeader(-2, Long.MAX_VALUE, -101).serialize(out, "reply");
        return bytes.toByteArray();
    }

    private static void require(boolean condition) throws IOException {
        if (!condition) throw new IOException("Oracle semantic comparison failed");
    }

    private static void verify(byte[] bytes) throws IOException {
        var stream = new ByteArrayInputStream(bytes);
        var in = BinaryInputArchive.getArchive(stream);
        for (int value : INTS) require(in.readInt("int") == value);
        for (long value : LONGS) require(in.readLong("long") == value);
        require(!in.readBool("bool"));
        require(in.readBool("bool"));
        for (String value : STRINGS) require(Objects.equals(in.readString("string"), value));
        require(in.readBuffer("buffer") == null);
        require(Arrays.equals(in.readBuffer("buffer"), new byte[0]));
        require(Arrays.equals(in.readBuffer("buffer"), new byte[] {0, (byte)255, (byte)128}));
        Index index = in.startVector("vector");
        for (String value : STRINGS) {
            require(!index.done());
            require(Objects.equals(in.readString("item"), value));
            index.incr();
        }
        require(index.done());
        in.endVector("vector");
        require(in.startVector("nullVector") == null);
        in.endVector("nullVector");
        var stat = new Stat();
        stat.deserialize(in, "stat");
        require(stat.equals(STAT));
        var request = new RequestHeader();
        request.deserialize(in, "request");
        require(request.equals(new RequestHeader(1, 4)));
        var reply = new ReplyHeader();
        reply.deserialize(in, "reply");
        require(reply.equals(new ReplyHeader(-2, Long.MAX_VALUE, -101)));
        require(stream.available() == 0);
    }

    public static void main(String[] args) throws IOException {
        Path path = Path.of(args[1]);
        if (args[0].equals("generate")) {
            byte[] bytes = encode();
            verify(bytes);
            Files.createDirectories(path.toAbsolutePath().getParent());
            Files.writeString(path, HexFormat.of().formatHex(bytes) + "\n");
        } else if (args[0].equals("verify")) {
            verify(HexFormat.of().parseHex(Files.readString(path).trim()));
        } else {
            throw new IllegalArgumentException("Expected generate or verify");
        }
    }
}
