import org.jetbrains.kotlin.gradle.dsl.JvmTarget

plugins {
    alias(libs.plugins.kotlin.multiplatform)
    alias(libs.plugins.android.library)
}

// libopus: the camera speaks Opus and no Apple framework decodes it. Homebrew's static libopus.a
// is linked into the binary, so the shipped app carries no dylib dependency.
val opusPrefix: String = System.getenv("OPUS_PREFIX")
    ?: providers.gradleProperty("opusPrefix").orNull
    ?: "/opt/homebrew/opt/opus"

kotlin {
    androidTarget {
        compilerOptions { jvmTarget.set(JvmTarget.JVM_17) }
    }

    macosArm64 {
        compilations.getByName("main") {
            cinterops.create("copus") {
                defFile(project.file("src/nativeInterop/cinterop/copus.def"))
                includeDirs("$opusPrefix/include")
                extraOpts("-libraryPath", "$opusPrefix/lib")
            }
        }
        // What Xcode links against. Static, so the shipped .app carries no dylib of ours.
        binaries.framework {
            baseName = "BabyMonitorCore"
            isStatic = true
        }
    }
    // Future shells plug in here and get the whole monitor for free:
    //   jvm()        -> Windows/Linux desktop (reuses jvmMain's sockets + HTTP)
    //   iosArm64()   -> iOS (reuses appleMain in full)

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
