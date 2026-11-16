package ru.wakemini.app;

import android.app.PendingIntent;
import android.appwidget.AppWidgetManager;
import android.appwidget.AppWidgetProvider;
import android.content.Context;
import android.content.Intent;
import android.net.Uri;
import android.os.Bundle;
import java.util.concurrent.atomic.AtomicBoolean;
import android.widget.RemoteViews;

import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

public class WakeWidgetProvider extends AppWidgetProvider {
    private static final String ACTION_REFRESH = "ru.wakemini.app.REFRESH_WIDGET";
    private static final String ACTION_TOGGLE = "ru.wakemini.app.TOGGLE_PC";
    private static final AtomicBoolean BUSY = new AtomicBoolean();
    private static final ExecutorService WORKER = Executors.newSingleThreadExecutor();

    @Override
    public void onUpdate(Context context, AppWidgetManager manager, int[] ids) {
        String status = cachedStatus(context);
        for (int id : ids) update(context, manager, id, status);
    }

    @Override
    public void onAppWidgetOptionsChanged(Context context, AppWidgetManager manager, int id, Bundle options) {
        update(context, manager, id, cachedStatus(context));
    }

    private static String cachedStatus(Context context) {
        android.content.SharedPreferences p = context.getSharedPreferences("wake_widget", Context.MODE_PRIVATE);
        // A stale command/check result must not masquerade as current computer state.
        return System.currentTimeMillis() - p.getLong("time", 0) < 60_000
                ? p.getString("status", "Компьютер") : "Компьютер";
    }

    static void showStatus(Context context, String status) {
        context.getSharedPreferences("wake_widget", Context.MODE_PRIVATE).edit()
                .putString("status", status).putLong("time", System.currentTimeMillis()).apply();
        AppWidgetManager manager = AppWidgetManager.getInstance(context);
        int[] ids = manager.getAppWidgetIds(new android.content.ComponentName(context, WakeWidgetProvider.class));
        for (int id : ids) update(context, manager, id, status);
        Intent refresh = new Intent(context, WakeWidgetProvider.class).setAction(ACTION_REFRESH);
        PendingIntent alarm = PendingIntent.getBroadcast(context, 0, refresh,
                PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE);
        android.app.AlarmManager alarms = (android.app.AlarmManager) context.getSystemService(Context.ALARM_SERVICE);
        // One inexact refresh, not recurring network polling; allowed to defer in Doze.
        alarms.set(android.app.AlarmManager.ELAPSED_REALTIME, android.os.SystemClock.elapsedRealtime()+61_000, alarm);
    }

    @Override
    public void onReceive(Context context, Intent intent) {
        if (ACTION_REFRESH.equals(intent.getAction())) {
            AppWidgetManager manager = AppWidgetManager.getInstance(context);
            onUpdate(context, manager, manager.getAppWidgetIds(new android.content.ComponentName(context, WakeWidgetProvider.class)));
            return;
        }
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
        if (!BUSY.compareAndSet(false, true)) return;
        android.content.BroadcastReceiver.PendingResult pending = goAsync();
        AppWidgetManager manager = AppWidgetManager.getInstance(context);
        showStatus(context, "Проверяю компьютер…");
        WORKER.execute(() -> {
            try {
                String result = WakeController.toggle(context);
                showStatus(context, result);
            } finally {
                BUSY.set(false);
                pending.finish();
            }
        });
    }

    private static void update(Context context, AppWidgetManager manager, int id, String status) {
        RemoteViews views = new RemoteViews(context.getPackageName(), R.layout.wake_widget);
        Bundle options = manager.getAppWidgetOptions(id);
        int side = WidgetButtonStyle.sizeDp(options.getInt(AppWidgetManager.OPTION_APPWIDGET_MIN_WIDTH),
                options.getInt(AppWidgetManager.OPTION_APPWIDGET_MIN_HEIGHT));
        views.setImageViewBitmap(R.id.widget_root, WidgetButtonRenderer.render(context, status, side));
        views.setContentDescription(R.id.widget_root, "Компьютер. " + status + ". Нажмите для включения или выключения.");
        Intent click = new Intent(context, WakeWidgetProvider.class).setAction(ACTION_TOGGLE)
                .setData(Uri.parse("wakemini://widget/" + id))
                .putExtra(AppWidgetManager.EXTRA_APPWIDGET_ID, id);
        PendingIntent pending = PendingIntent.getBroadcast(context, id, click,
                PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE);
        views.setOnClickPendingIntent(R.id.widget_root, pending);
        manager.updateAppWidget(id, views);
    }

}
