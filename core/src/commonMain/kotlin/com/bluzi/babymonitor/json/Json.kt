package com.bluzi.babymonitor.json

import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonArray as KxArray
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonNull
import kotlinx.serialization.json.JsonObject as KxObject
import kotlinx.serialization.json.JsonPrimitive

/**
 * A small, permissive JSON object model.
 *
 * Android ships `org.json` in the platform; nothing else does. Rather than rewrite the protocol
 * layer around a typed serializer — which would mean re-deriving from scratch exactly how the Mi
 * gateway's untyped, inconsistent responses are read — this keeps the same shape and the same
 * lenient `opt*` semantics, with a real parser (kotlinx.serialization) underneath.
 *
 * The lenience is the point: a missing or wrong-typed field reads as its default and never throws.
 * Xiaomi's responses vary by region, account state and firmware, and a monitor that dies on an
 * unexpected field at 3am is worse than one that carries on with a default.
 *
 * Values are held as String, Long, Double, Boolean, JSONObject, JSONArray or null.
 */
class JSONObject internal constructor(private val values: MutableMap<String, Any?>) {
    constructor() : this(mutableMapOf())

    constructor(json: String) : this(parseToObject(json))

    fun put(key: String, value: Any?): JSONObject {
        values[key] = normalize(value)
        return this
    }

    fun has(key: String): Boolean = values.containsKey(key)

    fun isNull(key: String): Boolean = values[key] == null

    fun opt(key: String): Any? = values[key]

    fun get(key: String): Any = values[key] ?: throw JsonException("no value for '$key'")

    fun getString(key: String): String = asString(get(key))

    /** Containers have no string form worth having — they read as the default, as in org.json. */
    fun optString(key: String, default: String = ""): String = when (val v = values[key]) {
        null, is JSONObject, is JSONArray -> default
        else -> asString(v)
    }

    fun optInt(key: String, default: Int = 0): Int = asNumber(values[key])?.toInt() ?: default

    fun optDouble(key: String, default: Double): Double = asNumber(values[key]) ?: default

    fun optBoolean(key: String, default: Boolean = false): Boolean = when (val v = values[key]) {
        is Boolean -> v
        is String -> v.toBooleanStrictOrNull() ?: default
        else -> default
    }

    fun getInt(key: String): Int =
        asNumber(get(key))?.toInt() ?: throw JsonException("'$key' is not a number")

    fun getJSONObject(key: String): JSONObject =
        values[key] as? JSONObject ?: throw JsonException("'$key' is not an object")

    fun getJSONArray(key: String): JSONArray =
        values[key] as? JSONArray ?: throw JsonException("'$key' is not an array")

    fun optJSONObject(key: String): JSONObject? = values[key] as? JSONObject

    fun optJSONArray(key: String): JSONArray? = values[key] as? JSONArray

    override fun toString(): String = buildString { writeObject(this, values) }
}

class JSONArray internal constructor(private val values: MutableList<Any?>) {
    constructor() : this(mutableListOf())

    fun put(value: Any?): JSONArray {
        values.add(normalize(value))
        return this
    }

    fun length(): Int = values.size

    fun opt(index: Int): Any? = values.getOrNull(index)

    fun getJSONObject(index: Int): JSONObject =
        values.getOrNull(index) as? JSONObject ?: throw JsonException("[$index] is not an object")

    fun optJSONObject(index: Int): JSONObject? = values.getOrNull(index) as? JSONObject

    override fun toString(): String = buildString { writeArray(this, values) }
}

class JsonException(message: String) : Exception(message)

// --- parsing ----------------------------------------------------------------

private val parser = Json { isLenient = true; ignoreUnknownKeys = true }

private fun parseToObject(json: String): MutableMap<String, Any?> {
    val element = try {
        parser.parseToJsonElement(json)
    } catch (e: Exception) {
        throw JsonException("not JSON: ${e.message}")
    }
    if (element !is KxObject) throw JsonException("not a JSON object")
    return element.mapValuesTo(mutableMapOf()) { (_, v) -> fromElement(v) }
}

private fun fromElement(element: JsonElement): Any? = when (element) {
    is JsonNull -> null
    is JsonPrimitive ->
        if (element.isString) {
            element.content
        } else {
            element.content.toLongOrNull()
                ?: element.content.toDoubleOrNull()
                ?: element.content.toBooleanStrictOrNull()
                ?: element.content
        }
    is KxObject -> JSONObject(element.mapValuesTo(mutableMapOf()) { (_, v) -> fromElement(v) })
    is KxArray -> JSONArray(element.mapTo(mutableListOf()) { fromElement(it) })
}

// --- coercion ---------------------------------------------------------------

private fun normalize(value: Any?): Any? = when (value) {
    is Int -> value.toLong()
    is Short -> value.toLong()
    is Byte -> value.toLong()
    is Float -> value.toDouble()
    else -> value
}

private fun asString(value: Any): String = when (value) {
    is String -> value
    is Double -> formatDouble(value)
    else -> value.toString()
}

private fun asNumber(value: Any?): Double? = when (value) {
    is Long -> value.toDouble()
    is Double -> value
    is Boolean -> if (value) 1.0 else 0.0
    is String -> value.toDoubleOrNull()
    else -> null
}

/** A whole-number double reads back as a whole number, not "5.0". */
private fun formatDouble(value: Double): String =
    if (value.isFinite() && value == value.toLong().toDouble()) value.toLong().toString()
    else value.toString()

// --- writing ----------------------------------------------------------------

private fun writeObject(out: StringBuilder, values: Map<String, Any?>) {
    out.append('{')
    var first = true
    for ((k, v) in values) {
        if (!first) out.append(',')
        first = false
        writeString(out, k)
        out.append(':')
        writeValue(out, v)
    }
    out.append('}')
}

private fun writeArray(out: StringBuilder, values: List<Any?>) {
    out.append('[')
    for ((i, v) in values.withIndex()) {
        if (i > 0) out.append(',')
        writeValue(out, v)
    }
    out.append(']')
}

private fun writeValue(out: StringBuilder, value: Any?) {
    when (value) {
        null -> out.append("null")
        is String -> writeString(out, value)
        is Boolean, is Long -> out.append(value.toString())
        is Double -> out.append(formatDouble(value))
        is JSONObject, is JSONArray -> out.append(value.toString())
        else -> writeString(out, value.toString())
    }
}

private fun writeString(out: StringBuilder, s: String) {
    out.append('"')
    for (c in s) {
        when {
            c == '"' -> out.append("\\\"")
            c == '\\' -> out.append("\\\\")
            c == '\n' -> out.append("\\n")
            c == '\r' -> out.append("\\r")
            c == '\t' -> out.append("\\t")
            c < ' ' -> out.append("\\u").append(c.code.toString(16).padStart(4, '0'))
            else -> out.append(c)
        }
    }
    out.append('"')
}
