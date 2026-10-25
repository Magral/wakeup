package ru.wakemini.app;

import android.app.PendingIntent;
import android.appwidget.AppWidgetManager;
import android.appwidget.AppWidgetProvider;
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
        for (int id : ids) update(context, manager, id, "ВКЛ / ВЫКЛ");
    }

    @Override
    public void onReceive(Context context, Intent intent) {
        if (!ACTION_TOGGLE.equals(intent.getAction())) {
            super.onReceive(context, intent);
            return;
        }
        int id = intent.getIntExtra(AppWidgetManager.EXTRA_APPWIDGET_ID, AppWidgetManager.INVALID_APPWIDGET_ID);
        android.content.SharedPreferences prefs = context.getSharedPreferences(WakeController.PREFS, Context.MODE_PRIVATE);
        String legacyAddress = prefs.getString("broadcast", "");
        String address = prefs.getString("address", legacyAddress).trim();
        if (prefs.getString("mac", "").trim().isEmpty() || address.isEmpty()) {
            update(context, AppWidgetManager.getInstance(context), id, "Настройте в приложении");
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
        views.setTextViewText(R.id.widget_status, compactStatus(status));
        Intent click = new Intent(context, WakeWidgetProvider.class).setAction(ACTION_TOGGLE)
                .setData(Uri.parse("wakemini://widget/" + id))
                .putExtra(AppWidgetManager.EXTRA_APPWIDGET_ID, id);
        PendingIntent pending = PendingIntent.getBroadcast(context, id, click,
                PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE);
        views.setOnClickPendingIntent(R.id.widget_root, pending);
        manager.updateAppWidget(id, views);
    }

    private static String compactStatus(String status) {
        if (status.startsWith("Команда выключения принята")) return "Выключаю ПК";
        if (status.startsWith("Пакет пробуждения отправлен") || status.startsWith("ПК не ответил")) return "Включаю ПК";
        if (status.startsWith("Проверяю")) return "Проверяю…";
        if (status.startsWith("Ошибка")) return "Ошибка";
        if (status.startsWith("Настройте в приложении")) return "Сначала настройте";
        return status;
    }
}
