package com.bluzi.babymonitor.data

import android.content.Context
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import com.bluzi.babymonitor.xiaomi.base64ToBytes
import com.bluzi.babymonitor.xiaomi.concatBytes
import com.bluzi.babymonitor.xiaomi.toBase64
import java.security.KeyStore
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

// Android edges of the persistence layer (AUTH-5/6): SharedPreferences + an AES/GCM key that
// never leaves the Android Keystore.

class PrefsKeyValueStore(context: Context) : KeyValueStore {
    private val prefs = context.applicationContext.getSharedPreferences("babymonitor", Context.MODE_PRIVATE)

    override fun get(key: String): String? = prefs.getString(key, null)

    override fun put(key: String, value: String) {
        prefs.edit().putString(key, value).apply()
    }

    override fun remove(key: String) {
        prefs.edit().remove(key).apply()
    }
}

class KeystoreSecretBox : SecretBox {
    private companion object {
        const val ALIAS = "babymonitor-session"
        const val TRANSFORM = "AES/GCM/NoPadding"
    }

    private fun key(): SecretKey {
        val store = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        (store.getKey(ALIAS, null) as? SecretKey)?.let { return it }
        val generator = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore")
        generator.init(
            KeyGenParameterSpec.Builder(ALIAS, KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT)
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                .build(),
        )
        return generator.generateKey()
    }

    override fun seal(plain: String): String {
        val cipher = Cipher.getInstance(TRANSFORM)
        cipher.init(Cipher.ENCRYPT_MODE, key())
        val sealed = concatBytes(cipher.iv, cipher.doFinal(plain.toByteArray()))
        return sealed.toBase64()
    }

    override fun open(sealed: String): String? = try {
        val bytes = sealed.base64ToBytes()
        val cipher = Cipher.getInstance(TRANSFORM)
        cipher.init(Cipher.DECRYPT_MODE, key(), GCMParameterSpec(128, bytes, 0, 12))
        String(cipher.doFinal(bytes, 12, bytes.size - 12))
    } catch (_: Exception) {
        null
    }
}

object Stores {
    @Volatile
    private var instance: AppStore? = null

    fun app(context: Context): AppStore =
        instance ?: synchronized(this) {
            instance ?: AppStore(PrefsKeyValueStore(context), KeystoreSecretBox()).also { instance = it }
        }
}
