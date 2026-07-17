import org.jetbrains.kotlin.gradle.dsl.JvmTarget
import org.jetbrains.kotlin.gradle.plugin.mpp.KotlinNativeTarget

plugins {
    alias(libs.plugins.kotlin.multiplatform)
    alias(libs.plugins.android.library)
}

// libopus: the camera speaks Opus and no Apple framework decodes it, so a static libopus.a is linked
// into each native binary and the shipped app carries no dylib dependency.
//
// Every Apple platform is its own platform with its own libopus: macOS gets Homebrew's (arm64
// macOS); iOS device and iOS simulator are cross-built by core/opus/build-ios-opus.sh into
// core/build/opus-ios/{device,sim}. Each prefix is overridable, exactly like OPUS_PREFIX, so CI or a
// different install location can be pointed at without editing this file.
val opusPrefix: String = System.getenv("OPUS_PREFIX")
    ?: providers.gradleProperty("opusPrefix").orNull
    ?: "/opt/homebrew/opt/opus"
val opusIosSimPrefix: String = System.getenv("OPUS_IOS_SIM_PREFIX")
    ?: providers.gradleProperty("opusIosSimPrefix").orNull
    ?: project.file("build/opus-ios/sim").absolutePath
val opusIosDevicePrefix: String = System.getenv("OPUS_IOS_DEVICE_PREFIX")
    ?: providers.gradleProperty("opusIosDevicePrefix").orNull
    ?: project.file("build/opus-ios/device").absolutePath

kotlin {
    androidTarget {
        compilerOptions { jvmTarget.set(JvmTarget.JVM_17) }
    }

    // Every Apple target links the same static libopus and exports the same static framework — what
    // the Swift shell (Xcode-less: swiftc + a bundle) links against. Only the opus build differs, so
    // the three targets share one configuration and pass their own prefix.
    val copusDef = project.file("src/nativeInterop/cinterop/copus.def")
    fun KotlinNativeTarget.babyMonitorCore(opusDir: String) {
        compilations.getByName("main").cinterops.create("copus") {
            defFile(copusDef)
            includeDirs("$opusDir/include")
            extraOpts("-libraryPath", "$opusDir/lib")
        }
        // Static, so the shipped app carries no dylib of ours — the binary carries the monitor,
        // libopus and all.
        binaries.framework {
            baseName = "BabyMonitorCore"
            isStatic = true
        }
    }

    macosArm64 { babyMonitorCore(opusPrefix) }
    // iOS reuses appleMain in full — the whole monitor, for free. Device and simulator are separate
    // platforms with separate opus builds; both are declared so the framework builds for a real
    // phone as well as the simulator we verify on.
    iosArm64 { babyMonitorCore(opusIosDevicePrefix) }
    iosSimulatorArm64 { babyMonitorCore(opusIosSimPrefix) }
    // A jvm() target would plug in here too -> Windows/Linux desktop (reuses jvmMain's sockets + HTTP)

    applyDefaultHierarchyTemplate()

    sourceSets {
        commonMain.dependencies {
            implementation(libs.kotlinx.coroutines.core)
            implementation(libs.kotlinx.serialization.json)
            implementation(libs.kotlinx.datetime)
        }
        commonTest.dependencies {
            implementation(kotlin("test"))
            implementation(libs.kotlinx.coroutines.test)
        }
        getByName("androidUnitTest").dependencies {
            implementation(kotlin("test-junit"))
            implementation(libs.bouncycastle) // reference impl for the crypto differential tests
        }
    }
}

android {
    namespace = "com.bluzi.babymonitor.core"
    compileSdk = 36
    defaultConfig { minSdk = 26 }
    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    testOptions {
        // Android's unit-test android.jar is all stubs that throw, and `elapsedRealtimeMs` is
        // SystemClock.elapsedRealtime — so anything that constructs the engine died here on the JVM
        // before it ran a line. That is why the engine's lifecycle had no test on this target while the
        // C# port's had one; the criteria that live in the engine were only ever pinned on one side.
        //
        // MIND THE TRAP THIS BUYS: with the stubs defaulted, that clock reads **0 for ever** on the JVM.
        // Nothing here may assert on time passing — a watchdog or backoff test would pass vacuously,
        // which is worse than not having it. Time-dependent behaviour belongs on the native targets
        // (macosArm64Test / iosSimulatorArm64Test), where the clock is real, and `allTests` runs them.
        unitTests.isReturnDefaultValues = true
    }
}

// `check` must mean every platform, or the promise that both apps behave the same is worth
// nothing. Android's unit tests come in via the AGP plugin; this adds the native ones, so a
// change that breaks the monitor on macOS alone cannot go green.
tasks.named("check") { dependsOn("allTests") }

// ---------------------------------------------------------------------------
// Interop vectors. The reference bytes are a JSON file (regenerated from the proven c100 TS
// implementation), but Kotlin/Native has no classpath resources — so the file is compiled into
// the test binary as a string. That is what lets the SAME vector tests run on the JVM and on
// macOS: the protocol is proven identical on both, not merely assumed to be.
// ---------------------------------------------------------------------------
val generateProtocolVectors by tasks.registering {
    val input = layout.projectDirectory.file("protocol-vectors.json")
    val outputDir = layout.buildDirectory.dir("generated/protocolVectors")
    inputs.file(input)
    outputs.dir(outputDir)
    doLast {
        val json = input.asFile.readText()
        val literal = json
            .replace("\\", "\\\\")
            .replace("\"", "\\\"")
            .replace("\n", "\\n")
            .replace("\r", "")
            .replace("$", "\\$")
        val out = outputDir.get().file("ProtocolVectors.kt").asFile
        out.parentFile.mkdirs()
        out.writeText(
            """
            |package com.bluzi.babymonitor.xiaomi
            |
            |// GENERATED from core/protocol-vectors.json — do not edit.
            |internal const val PROTOCOL_VECTORS_JSON: String = "$literal"
            |
            """.trimMargin(),
        )
    }
}

kotlin.sourceSets.commonTest {
    kotlin.srcDir(generateProtocolVectors)
}
