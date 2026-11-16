package ru.wakemini.app;

import android.content.Context;
import android.graphics.Bitmap;
import android.graphics.Canvas;
import android.graphics.Color;
import android.graphics.Paint;
import android.graphics.RectF;
import android.graphics.Typeface;

/** Draw the power symbol as geometry; no dependency on a launcher's font glyphs. */
final class WidgetButtonRenderer {
    static Bitmap render(Context context, String status, int sizeDp) {
        float density = context.getResources().getDisplayMetrics().density;
        int pixels = Math.max(40, Math.round(sizeDp * density));
        Bitmap bitmap = Bitmap.createBitmap(pixels, pixels, Bitmap.Config.ARGB_8888);
        bitmap.setDensity(context.getResources().getDisplayMetrics().densityDpi);
        Canvas canvas = new Canvas(bitmap);
        canvas.scale(pixels / 100f, pixels / 100f);
        WidgetButtonStyle style = WidgetButtonStyle.from(status);
        Paint paint = new Paint(Paint.ANTI_ALIAS_FLAG);
        paint.setColor(0x28000000);
        canvas.drawCircle(50, 52, 46, paint);
        paint.setColor(0xff17252f);
        canvas.drawCircle(50, 49, 45, paint);
        paint.setStyle(Paint.Style.STROKE);
        paint.setStrokeWidth(1.25f);
        paint.setColor(0xff39505c);
        canvas.drawCircle(50, 49, 44.4f, paint);
        paint.setColor(style.color);
        paint.setAlpha(style.busy ? 255 : 110);
        paint.setStrokeWidth(2.5f);
        canvas.drawArc(new RectF(6, 5, 94, 93), -90, style.busy ? 270 : 360, false, paint);
        paint.setAlpha(255);
        paint.setColor(style.color);
        paint.setStrokeWidth(4.5f);
        paint.setStrokeCap(Paint.Cap.ROUND);
        boolean compact = sizeDp < 72;
        float centerY = compact ? 47 : 40;
        // A standard power arc and vertical stem, rather than Unicode U+23FB.
        canvas.drawArc(new RectF(34, centerY - 16, 66, centerY + 16), -48, 276, false, paint);
        canvas.drawLine(50, centerY - 20, 50, centerY - 2, paint);
        paint.setStyle(Paint.Style.FILL);
        paint.setTypeface(Typeface.create("sans-serif-medium", Typeface.NORMAL));
        paint.setTextAlign(Paint.Align.CENTER);
        paint.setTextSize(compact ? 20 : 11.5f);
        paint.setColor(Color.WHITE);
        canvas.drawText(compact ? "ПК" : style.label, 50, compact ? 79 : 77, paint);
        return bitmap;
    }
}
