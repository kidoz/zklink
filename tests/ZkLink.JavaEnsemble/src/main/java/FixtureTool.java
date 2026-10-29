import java.nio.charset.StandardCharsets;
import java.nio.file.Path;
import java.util.Arrays;
import java.util.UUID;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import org.apache.zookeeper.CreateMode;
import org.apache.zookeeper.Version;
import org.apache.zookeeper.Watcher;
import org.apache.zookeeper.ZooDefs;
import org.apache.zookeeper.ZooKeeper;
import org.apache.zookeeper.client.ZKClientConfig;

/** Short-lived authenticated readiness; credentials and exception details never reach output. */
public final class FixtureTool {
    private static final int SESSION_TIMEOUT_MILLISECONDS = 5_000;
    private static final int CONNECT_TIMEOUT_SECONDS = 5;
    private static final int ANY_VERSION = -1;

    private FixtureTool() { }

    public static void main(String[] args) {
        try {
            if (args.length == 2 && args[0].equals("version")) {
                require(Version.getVersion().equals(args[1]));
                System.out.println("ZooKeeper runtime version: " + Version.getVersion());
            } else if (args.length == 3 && args[0].equals("probe")) {
                probe(args[1], args[2]);
                System.out.println("Authenticated create/read/delete: PASS");
            } else {
                throw new IllegalArgumentException("Invalid fixture command.");
            }
        } catch (Exception error) {
            System.err.println("Fixture check failed: " + error.getClass().getSimpleName());
            System.exit(1);
        }
    }

    private static void probe(String endpoint, String mode) throws Exception {
        require(mode.equals("secure") || mode.equals("plaintext"));
        ZKClientConfig config = new ZKClientConfig();
        config.setProperty("zookeeper.sasl.client", "false");
        if (mode.equals("secure")) {
            Path root = Path.of(required("ZKLINK_AOT_CERTIFICATE_DIRECTORY"));
            config.setProperty("zookeeper.client.secure", "true");
            config.setProperty("zookeeper.clientCnxnSocket", "org.apache.zookeeper.ClientCnxnSocketNetty");
            config.setProperty("zookeeper.ssl.keyStore.location", root.resolve("client.p12").toString());
            config.setProperty("zookeeper.ssl.keyStore.passwordPath", root.resolve("server/store-password").toString());
            config.setProperty("zookeeper.ssl.keyStore.type", "PKCS12");
            config.setProperty("zookeeper.ssl.trustStore.location", root.resolve("server/trust.p12").toString());
            config.setProperty("zookeeper.ssl.trustStore.passwordPath", root.resolve("server/store-password").toString());
            config.setProperty("zookeeper.ssl.trustStore.type", "PKCS12");
            config.setProperty("zookeeper.ssl.sslProvider", "JDK");
        }
        CountDownLatch connected = new CountDownLatch(1);
        byte[] auth = (required("ZKLINK_USER") + ":" + required("ZKLINK_PASSWORD")).getBytes(StandardCharsets.UTF_8);
        try (ZooKeeper client = new ZooKeeper(endpoint, SESSION_TIMEOUT_MILLISECONDS, event -> {
            if (event.getState() == Watcher.Event.KeeperState.SyncConnected) {
                connected.countDown();
            }
        }, config)) {
            require(connected.await(CONNECT_TIMEOUT_SECONDS, TimeUnit.SECONDS));
            client.addAuthInfo("digest", auth);
            String path = "/zklink-native-readiness-" + UUID.randomUUID();
            byte[] expected = "ready".getBytes(StandardCharsets.UTF_8);
            client.create(path, expected, ZooDefs.Ids.CREATOR_ALL_ACL, CreateMode.EPHEMERAL);
            require(Arrays.equals(expected, client.getData(path, false, null)));
            client.delete(path, ANY_VERSION);
            require(client.exists(path, false) == null);
        } finally {
            Arrays.fill(auth, (byte) 0);
        }
    }

    private static String required(String name) {
        String value = System.getenv(name);
        if (value == null || value.isEmpty()) {
            throw new IllegalArgumentException("Required fixture configuration is missing.");
        }
        return value;
    }

    private static void require(boolean condition) {
        if (!condition) {
            throw new IllegalStateException("Fixture invariant failed.");
        }
    }
}
