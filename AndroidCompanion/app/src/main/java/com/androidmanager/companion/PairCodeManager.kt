package com.androidmanager.companion

import kotlin.random.Random

object PairCodeManager {
    fun generate(): String = String.format("%06d", Random.nextInt(0, 1_000_000))
}
