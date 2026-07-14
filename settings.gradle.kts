pluginManagement {
    repositories {
        google()
        mavenCentral()
        gradlePluginPortal()
    }
}

dependencyResolutionManagement {
    repositoriesMode.set(RepositoriesMode.FAIL_ON_PROJECT_REPOS)
    repositories {
        google()
        mavenCentral()
    }
}

rootProject.name = "c100-baby-monitor"

// One core, many shells. `core` is the whole monitor — protocol, engine, alarm, DSP — and every
// app is a thin platform shell over it. Windows (a jvm target) and iOS slot into core the same way.
include(":core")
include(":android")
