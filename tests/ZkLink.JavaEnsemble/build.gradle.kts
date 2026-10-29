plugins { java }

repositories { mavenCentral() }

val zooKeeper387 = configurations.create("zooKeeper387")
val zooKeeper396 = configurations.create("zooKeeper396")

dependencies {
    // Compile against the older API; run the same helper against each isolated graph.
    compileOnly("org.apache.zookeeper:zookeeper:3.8.7") { isTransitive = false }
    compileOnly("org.apache.zookeeper:zookeeper-jute:3.8.7") { isTransitive = false }
    for ((configuration, version) in listOf(zooKeeper387 to "3.8.7", zooKeeper396 to "3.9.6")) {
        add(configuration.name, "org.apache.zookeeper:zookeeper:$version") {
            // Portable fixture uses Java NIO and JSSE, not optional native Netty transports.
            exclude(group = "io.netty", module = "netty-transport-native-epoll")
            exclude(group = "io.netty", module = "netty-tcnative-boringssl-static")
        }
        add(configuration.name, "io.dropwizard.metrics:metrics-core:4.1.12.1")
        // ZooKeeper references Epoll.isAvailable even when falling back to Java NIO.
        add(configuration.name, "io.netty:netty-transport-classes-epoll:4.1.137.Final")
        add(configuration.name, "org.xerial.snappy:snappy-java:1.1.10.5")
        add(configuration.name, "ch.qos.logback:logback-classic:1.3.15")
    }
}

dependencyLocking {
    lockAllConfigurations()
    lockMode.set(org.gradle.api.artifacts.dsl.LockMode.STRICT)
}
layout.buildDirectory.set(layout.projectDirectory.dir("../../artifacts/java-ensemble-build"))
tasks.withType<JavaCompile>().configureEach {
    options.release.set(17)
    options.encoding = "UTF-8"
}

tasks.register("exportRuntime") {
    dependsOn(tasks.classes)
    doLast {
        val output = file(providers.gradleProperty("fixtureOutput").get())
        require(!output.exists()) { "Fixture export requires a fresh output directory." }
        for ((configuration, version) in listOf(zooKeeper387 to "3.8.7", zooKeeper396 to "3.9.6")) {
            val destination = output.resolve(version)
            copy { from(sourceSets.main.get().output); into(destination.resolve("classes")) }
            val artifacts = configuration.incoming.artifacts.artifacts.sortedBy { it.file.name }
            copy { from(artifacts.map { it.file }); into(destination.resolve("lib")) }
            destination.resolve("dependencies.tsv").writeText(artifacts.joinToString("\n", postfix = "\n") {
                "${it.file.name}\t${it.id.componentIdentifier.displayName}"
            })
        }
    }
}
