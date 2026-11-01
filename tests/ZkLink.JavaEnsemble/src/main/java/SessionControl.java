import java.util.Arrays;
import java.util.Map;
import javax.management.MBeanServerConnection;
import javax.management.ObjectName;
import javax.management.remote.JMXConnector;
import javax.management.remote.JMXConnectorFactory;
import javax.management.remote.JMXServiceURL;

/** Container-local control of one exact session; never reads or prints credentials. */
public final class SessionControl {
    private static final String LOCAL_JMX = "service:jmx:rmi:///jndi/rmi://127.0.0.1:1099/jmxrmi";
    private static final long REQUEST_TIMEOUT_MILLISECONDS = 5_000;

    private SessionControl() { }

    public static void main(String[] args) {
        try {
            if (args.length != 1 || !args[0].matches("[0-9a-f]{1,16}")) {
                throw new IllegalArgumentException();
            }
            String expected = "0x" + Long.toHexString(Long.parseUnsignedLong(args[0], 16));
            if (expected.equals("0x0")) {
                throw new IllegalArgumentException();
            }
            try (JMXConnector connector = JMXConnectorFactory.connect(new JMXServiceURL(LOCAL_JMX),
                    Map.of("jmx.remote.x.request.waiting.timeout", REQUEST_TIMEOUT_MILLISECONDS))) {
                MBeanServerConnection server = connector.getMBeanServerConnection();
                ObjectName selected = null;
                for (ObjectName name : server.queryNames(new ObjectName("org.apache.ZooKeeperService:*"), null)) {
                    boolean connection = Arrays.stream(server.getMBeanInfo(name).getOperations())
                            .anyMatch(operation -> operation.getName().equals("terminateSession"));
                    if (connection && expected.equals(server.getAttribute(name, "SessionId"))) {
                        if (selected != null) {
                            throw new IllegalStateException();
                        }
                        selected = name;
                    }
                }
                if (selected == null) {
                    throw new IllegalStateException();
                }
                server.invoke(selected, "terminateSession", new Object[0], new String[0]);
            }
            System.out.println("Exact owned session termination requested: PASS");
        } catch (Exception error) {
            System.err.println("Session control failed: " + error.getClass().getSimpleName());
            System.exit(1);
        }
    }
}
