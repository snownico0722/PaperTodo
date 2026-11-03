package io.github.papertodo.mobile

import android.app.Application
import io.github.papertodo.mobile.data.PaperRepository

class PaperApp : Application() {
    lateinit var repository: PaperRepository
        private set

    override fun onCreate() {
        super.onCreate()
        repository = PaperRepository(this)
    }
}
