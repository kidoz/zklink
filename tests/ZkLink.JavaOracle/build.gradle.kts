plugins { java }

repositories { mavenCentral() }

// Java is a verification tool only. These jars are never driver dependencies.
dependencies {
    implementation("org.apache.zookeeper:zookeeper-jute:3.9.6") { isTransitive = false }
    implementation("org.apache.zookeeper:zookeeper:3.9.6") { isTransitive = false }
    runtimeOnly("org.slf4j:slf4j-api:2.0.17")
}

dependencyLocking {
    lockAllConfigurations()
    lockMode.set(org.gradle.api.artifacts.dsl.LockMode.STRICT)
}
layout.buildDirectory.set(layout.projectDirectory.dir("../../artifacts/java-oracle-build"))
tasks.withType<JavaCompile>().configureEach {
    options.release.set(17)
    options.encoding = "UTF-8"
}

val oracleOutput = providers.gradleProperty("oracleOutput").orElse("../../artifacts/jute-oracle/gradle")
val csharpOutput = providers.gradleProperty("csharpOutput").orElse("../../artifacts/jute-oracle/csharp")

tasks.register<JavaExec>("generatePrimitiveFixtures") {
    dependsOn(tasks.classes)
    classpath = sourceSets.main.get().runtimeClasspath
    mainClass.set("JuteOracle")
    args("generate", file(oracleOutput.get()).resolve("jute-records.hex").absolutePath)
}
tasks.register<JavaExec>("generateProtocolFixtures") {
    dependsOn(tasks.classes)
    classpath = sourceSets.main.get().runtimeClasspath
    mainClass.set("ProtocolOracle")
    args("generate", file(oracleOutput.get()).resolve("Protocol").absolutePath)
}
tasks.register("generateFixtures") { dependsOn("generatePrimitiveFixtures", "generateProtocolFixtures") }

tasks.register("verifyFixtures") {
    dependsOn("generateFixtures")
    doLast {
        val generated = file(oracleOutput.get())
        val expected = file("../ZkLink.Protocol.Tests/Fixtures")
        fun hexFiles(root: File) = root.walkTopDown().filter { it.isFile && it.extension in setOf("hex", "json") }
            .associateBy { it.relativeTo(root).invariantSeparatorsPath }
        val actualFiles = hexFiles(generated)
        val expectedFiles = hexFiles(expected)
        if (actualFiles.keys != expectedFiles.keys) throw GradleException("Generated fixture file set differs from checked-in fixtures")
        expectedFiles.forEach { (name, value) ->
            if (!value.readBytes().contentEquals(actualFiles.getValue(name).readBytes()))
                throw GradleException("Java fixture changed: $name")
        }
    }
}
tasks.named("check") { dependsOn("verifyFixtures") }

tasks.register<JavaExec>("verifyPrimitiveRequests") {
    dependsOn(tasks.classes)
    classpath = sourceSets.main.get().runtimeClasspath
    mainClass.set("JuteOracle")
    args("verify", file(csharpOutput.get()).resolve("jute-records.hex").absolutePath)
}
tasks.register<JavaExec>("verifyProtocolRequests") {
    dependsOn(tasks.classes)
    classpath = sourceSets.main.get().runtimeClasspath
    mainClass.set("ProtocolOracle")
    args("verify", file(csharpOutput.get()).resolve("Protocol").absolutePath)
}
tasks.register("verifyCSharpRequests") { dependsOn("verifyPrimitiveRequests", "verifyProtocolRequests") }

tasks.wrapper {
    gradleVersion = "9.8.2"
    distributionType = Wrapper.DistributionType.BIN
    distributionSha256Sum = "21bd5da2de49b642904077f38c0efdc7778e92b3ed0889392dca61b97e389607"
}
