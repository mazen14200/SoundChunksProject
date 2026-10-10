# Quran Cache Cleanup Background Service

## English

### Overview
Implemented a background service that automatically cleans up the Quran audio cache on a daily schedule.

### Task Description
Added a hosted background service that runs every day at 3:30 AM Egypt Standard Time to remove old cache files from:
- `App_Data/QuranCache/merged` - Merged MP3 files for ayah ranges
- `App_Data/QuranCache/peaks` - Waveform peak data JSON files

### Changes Made

#### 1. Made `TrimCache()` Public
- **File**: `Services/IQuranAudioService.cs`
- **File**: `Services/QuranAudioService.cs`
- Changed `TrimCache()` from private to public to allow external access by the background service

#### 2. Created Background Service
- **File**: `Services/QuranCacheCleanupService.cs`
- New hosted service that:
  - Calculates time until next 3:30 AM Egypt time
  - Waits for the calculated duration
  - Executes `TrimCache()` to clean old files
  - Repeats every 24 hours
  - Logs detailed information about scheduled runs and cleanup results

#### 3. Registered Background Service
- **File**: `Program.cs`
- Added `builder.Services.AddHostedService<QuranCacheCleanupService>()`

#### 4. Updated Cache Age
- **File**: `Services/QuranAudioService.cs`
- Changed `CacheMaxAge` from 14 days to **2 days**
- Files older than 2 days will be deleted

#### 5. Improved Cache Cleanup
- **File**: `Services/QuranAudioService.cs`
- Added `TrimCache()` call after building peaks cache to ensure immediate cleanup

### Configuration
- **Cleanup Time**: 3:30 AM Egypt Standard Time
- **Cache Age Limit**: 2 days
- **Time Zone**: Egypt Standard Time (includes DST support)

### How It Works
1. On application startup, the service calculates the time until next 3:30 AM Egypt time
2. It waits for that duration
3. At the scheduled time, it calls `TrimCache()` which:
   - Checks all JSON manifests in the merged directory
   - Deletes MP3 and JSON files older than 2 days
   - Checks all JSON files in the peaks directory
   - Deletes peaks files older than 2 days
4. The service then waits 24 hours for the next run
5. This cycle continues indefinitely until the application stops

### Benefits
- Automatic cache management without manual intervention
- Prevents disk space from growing indefinitely
- Cleanup happens during low-traffic hours (3:30 AM)
- Respects Egypt time zone and daylight saving time changes

---

## العربية

### نظرة عامة
تم تنفيذ خدمة خلفية (Background Service) تقوم بتنظيف كاش الصوتيات القرآنية تلقائياً على جدول يومي.

### وصف المهمة
إضافة خدمة خلفية تعمل كل يوم الساعة 3:30 صباحاً بتوقيت مصر لحذف ملفات الكاش القديمة من:
- `App_Data/QuranCache/merged` - ملفات MP3 المدمجة لمدى الآيات
- `App_Data/QuranCache/peaks` - ملفات JSON لبيانات الموجة الصوتية

### التغييرات المنفذة

#### 1. جعل `TrimCache()` عامة
- **الملف**: `Services/IQuranAudioService.cs`
- **الملف**: `Services/QuranAudioService.cs`
- تغيير `TrimCache()` من private إلى public للسماح بالوصول الخارجي من الخدمة الخلفية

#### 2. إنشاء الخدمة الخلفية
- **الملف**: `Services/QuranCacheCleanupService.cs`
- خدمة خلفية جديدة تقوم بـ:
  - حساب الوقت حتى الساعة 3:30 صباحاً بتوقيت مصر
  - الانتظار للمدة المحسوبة
  - تنفيذ `TrimCache()` لتنظيف الملفات القديمة
  - التكرار كل 24 ساعة
  - تسجيل معلومات تفصيلية عن الجدول الزمني ونتائج التنظيف

#### 3. تسجيل الخدمة الخلفية
- **الملف**: `Program.cs`
- إضافة `builder.Services.AddHostedService<QuranCacheCleanupService>()`

#### 4. تحديث عمر الكاش
- **الملف**: `Services/QuranAudioService.cs`
- تغيير `CacheMaxAge` من 14 يوم إلى **يومين**
- الملفات الأقدم من يومين سيتم حذفها

#### 5. تحسين تنظيف الكاش
- **الملف**: `Services/QuranAudioService.cs`
- إضافة استدعاء `TrimCache()` بعد بناء كاش peaks لضمان التنظيف الفوري

### الإعدادات
- **وقت التنظيف**: 3:30 صباحاً بتوقيت مصر القياسي
- **حد عمر الكاش**: يومان
- **المنطقة الزمنية**: توقيت مصر القياسي (مع دعم التوقيت الصيفي)

### كيف يعمل
1. عند بدء التطبيق، الخدمة تحسب الوقت حتى الساعة 3:30 صباحاً التالية بتوقيت مصر
2. تنتظر هذه المدة
3. في الوقت المحدد، تستدعي `TrimCache()` الذي:
   - يفحص جميع ملفات JSON manifests في مجلد merged
   - يحذف ملفات MP3 وJSON الأقدم من يومين
   - يفحص جميع ملفات JSON في مجلد peaks
   - يحذف ملفات peaks الأقدم من يومين
4. الخدمة تنتظر 24 ساعة للتنفيذ التالي
5. تستمر هذه الدورة إلى أن يتوقف التطبيق

### الفوائد
- إدارة الكاش تلقائية بدون تدخل يدوي
- منع نمو مساحة القرص بشكل غير محدود
- التنظيف يحدث في ساعات الانخفاض في الحركة (3:30 صباحاً)
- احترام توقيت مصر وتغييرات التوقيت الصيفي
