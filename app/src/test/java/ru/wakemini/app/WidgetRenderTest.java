package ru.wakemini.app;

import android.content.Context;
import android.graphics.Bitmap;
import android.graphics.Canvas;
import android.graphics.Color;
import android.graphics.Paint;
import android.view.View;
import android.widget.FrameLayout;
import android.widget.ImageView;
import android.widget.RemoteViews;
import java.io.File;
import java.io.FileOutputStream;
import org.junit.Test;
import org.junit.runner.RunWith;
import org.robolectric.RobolectricTestRunner;
import org.robolectric.RuntimeEnvironment;
import org.robolectric.annotation.Config;
import org.robolectric.annotation.GraphicsMode;
import static org.junit.Assert.*;

@RunWith(RobolectricTestRunner.class)
@Config(sdk = 28, qualifiers = "mdpi")
@GraphicsMode(GraphicsMode.Mode.NATIVE)
public class WidgetRenderTest {
    @Test public void circleRemainsSquareInsideWideWidgetAndPowerIsDrawn() throws Exception {
        Context context = RuntimeEnvironment.getApplication();
        Bitmap button = WidgetButtonRenderer.render(context, "Компьютер", 96);
        assertEquals(button.getWidth(), button.getHeight());
        assertEquals(0, Color.alpha(button.getPixel(0, 0)));
        assertTrue(Color.alpha(button.getPixel(50, 50)) > 0);
        RemoteViews remote = new RemoteViews(context.getPackageName(), R.layout.wake_widget);
        remote.setImageViewBitmap(R.id.widget_root, button);
        FrameLayout host = new FrameLayout(context);
        View inflated = remote.apply(context, host);
        host.addView(inflated);
        host.measure(View.MeasureSpec.makeMeasureSpec(360, View.MeasureSpec.EXACTLY),
                View.MeasureSpec.makeMeasureSpec(180, View.MeasureSpec.EXACTLY));
        host.layout(0, 0, 360, 180);
        ImageView image = inflated.findViewById(R.id.widget_root);
        assertEquals(ImageView.ScaleType.CENTER_INSIDE, image.getScaleType());
        Bitmap wide = Bitmap.createBitmap(360, 180, Bitmap.Config.ARGB_8888);
        host.draw(new Canvas(wide));
        // The transparent surrounding cell must not become a stretched oval panel.
        int left = 360, right = -1, top = 180, bottom = -1;
        for (int y=0; y<180; y++) for (int x=0; x<360; x++) if (Color.alpha(wide.getPixel(x,y)) > 0) {
            left=Math.min(left,x); right=Math.max(right,x); top=Math.min(top,y); bottom=Math.max(bottom,y);
        }
        assertTrue("Widget contains no visible pixels", right > left);
        assertTrue("Circle stretched inside non-square host", Math.abs((right-left)-(bottom-top)) <= 5);
        assertTrue("Widget became oversized", right-left <= 96);
        // Produce a real Android-rendered review image, not a design mockup.
        Bitmap preview = Bitmap.createBitmap(900, 380, Bitmap.Config.ARGB_8888);
        Canvas canvas = new Canvas(preview); canvas.drawColor(0xff0f1922);
        Paint text = new Paint(Paint.ANTI_ALIAS_FLAG); text.setColor(Color.WHITE); text.setTextSize(23);
        canvas.drawText("Wake Mini · Power widget", 36, 45, text);
        String[] states = {"Компьютер", "ПК включён · 60 мс", "Проверяю компьютер…", "Ошибка: нет связи"};
        String[] labels = {"Ready", "PC online", "Checking", "Error"};
        for (int i=0; i<4; i++) {
            canvas.save(); canvas.translate(48+i*215, 82); canvas.scale(1.5f,1.5f);
            canvas.drawBitmap(WidgetButtonRenderer.render(context, states[i], 96), 0, 0, null); canvas.restore();
            canvas.drawText(labels[i], 62+i*215, 270, text);
        }
        text.setTextSize(18); canvas.drawText("1×1 compact · drawn power icon · no oval stretching", 36, 340, text);
        File file = new File("build/reports/widget-preview.png"); file.getParentFile().mkdirs();
        try (FileOutputStream stream = new FileOutputStream(file)) { assertTrue(preview.compress(Bitmap.CompressFormat.PNG,100,stream)); }
    }
}
