package ru.wakemini.app;

import android.app.PendingIntent;
import android.appwidget.AppWidgetManager;
import android.appwidget.AppWidgetProvider;
import android.content.ComponentName;
import android.content.Context;
import android.content.Intent;
import android.net.Uri;
import android.widget.RemoteViews;

import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

public class WakeWidgetProvider extends AppWidgetProvider {
    private static final String ACTION_TOGGLE = "ru.wakemini.app.TOGGLE_PC";
    private static final ExecutorService WORKER = Executors.newSingleThreadExecutor();

    @Override
    public void onUpdate(Context context, AppWidgetManager manager, int[] ids) {
        for (int id : ids) update(context, manager, id, "Нажмите для управления");
    }

    @Override
    public void onReceive(Context context, Intent intent) {
        if (!ACTION_TOGGLE.equals(intent.getAction())) {
            super.onReceive(context, intent);
            return;
        }
        int id = intent.getIntExtra(AppWidgetManager.EXTRA_APPWIDGET_ID, AppWidgetManager.INVALID_APPWIDGET_ID);
        android.content.SharedPreferences prefs = context.getSharedPreferences(WakeController.PREFS, Context.MODE_PRIVATE);
        if (prefs.getString("mac", "").trim().isEmpty() || prefs.getString("address", "").trim().isEmpty()) {
            Intent setup = new Intent(context, MainActivity.class).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
            context.startActivity(setup);
            return;
        }
        android.content.BroadcastReceiver.PendingResult pending = goAsync();
        AppWidgetManager manager = AppWidgetManager.getInstance(context);
        update(context, manager, id, "Проверяю компьютер…");
        WORKER.execute(() -> {
            try {
                String result = WakeController.toggle(context);
                update(context, manager, id, result);
            } finally {
                pending.finish();
            }
        });
    }

    private static void update(Context context, AppWidgetManager manager, int id, String status) {
        RemoteViews views = new RemoteViews(context.getPackageName(), R.layout.wake_widget);
        views.setTextViewText(R.id.widget_status, status);
        Intent click = new Intent(context, WakeWidgetProvider.class).setAction(ACTION_TOGGLE)
                .setData(Uri.parse("wakemini://widget/" + id))
                .putExtra(AppWidgetManager.EXTRA_APPWIDGET_ID, id);
        PendingIntent pending = PendingIntent.getBroadcast(context, id, click,
                PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE);
        views.setOnClickPendingIntent(R.id.widget_root, pending);
        manager.updateAppWidget(id, views);
    }
}
