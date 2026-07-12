import org.jetbrains.kotlin.gradle.dsl.JvmTarget

plugins {
    alias(libs.plugins.android.application)
    alias(libs.plugins.kotlin.android)
    alias(libs.plugins.kotlin.compose)
}

// CI derives these from the commit count on main (see .github/workflows/release.yml);
// local builds fall back to the defaults below.
val releaseVersionName = providers.gradleProperty("releaseVersionName").orNull
val releaseVersionCode = providers.gradleProperty("releaseVersionCode").orNull?.toInt()

// Release signing is injected via environment so CI (and deliberate local release builds)
// can sign; without it assembleRelease produces an unsigned APK.
val keystoreFile: String? = System.getenv("BM_KEYSTORE_FILE")

android {
    namespace = "com.bluzi.babymonitor"
    compileSdk = 36

    defaultConfig {
        applicationId = "com.bluzi.babymonitor"
        minSdk = 26
        targetSdk = 36
        versionCode = releaseVersionCode ?: 1
        versionName = releaseVersionName ?: "0.1.0"
    }

    if (keystoreFile != null) {
        signingConfigs {
            create("release") {
                storeFile = file(keystoreFile)
                storePassword = System.getenv("BM_KEYSTORE_PASSWORD")
                keyAlias = System.getenv("BM_KEY_ALIAS")
                keyPassword = System.getenv("BM_KEYSTORE_PASSWORD")
            }
        }
    }

    buildTypes {
        release {
            isMinifyEnabled = true
            proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"), "proguard-rules.pro")
            signingConfig = signingConfigs.findByName("release")
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    buildFeatures {
        compose = true
    }
}

kotlin {
    compilerOptions {
        jvmTarget.set(JvmTarget.JVM_17)
    }
}

dependencies {
    implementation(libs.androidx.core.ktx)
    implementation(libs.androidx.activity.compose)
    implementation(libs.androidx.lifecycle.runtime.ktx)
    implementation(libs.compose.ui)
    implementation(libs.compose.foundation)
    implementation(libs.compose.material3)
    implementation(libs.compose.material.icons)
    implementation(libs.kotlinx.coroutines.android)
    implementation(libs.bouncycastle)

    testImplementation(libs.junit)
    testImplementation(libs.json)
    testImplementation(libs.kotlinx.coroutines.test)
}

// ---------------------------------------------------------------------------
// Task-runner entry points (see CLAUDE.md "Commands"):
//   ./gradlew runEmulator   build + install + launch on a running emulator
//   ./gradlew runPhone      build + install + launch on a USB-connected phone
// ---------------------------------------------------------------------------

val adbPath: String =
    System.getenv("ANDROID_HOME")?.let { "$it/platform-tools/adb" }
        ?: "${System.getProperty("user.home")}/Library/Android/sdk/platform-tools/adb"

fun registerRunTask(name: String, adbTargetFlag: String, targetDescription: String) {
    tasks.register(name) {
        group = "run"
        description = "Installs and launches the debug app on $targetDescription"
        dependsOn("assembleDebug")
        doLast {
            val apk = layout.buildDirectory
                .file("outputs/apk/debug/app-debug.apk").get().asFile.absolutePath
            providers.exec {
                commandLine(adbPath, adbTargetFlag, "install", "-r", apk)
            }.result.get().assertNormalExitValue()
            providers.exec {
                commandLine(
                    adbPath, adbTargetFlag, "shell", "am", "start",
                    "-n", "com.bluzi.babymonitor/.MainActivity",
                )
            }.result.get().assertNormalExitValue()
        }
    }
}

registerRunTask("runEmulator", "-e", "the running emulator")
registerRunTask("runPhone", "-d", "the USB-connected phone")
