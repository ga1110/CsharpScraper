# PowerShell скрипт для создания бэкапа Elasticsearch индекса
param(
    [string]$BackupPath = ".\elasticsearch_backup",
    [string]$ElasticsearchUrl = "http://localhost:9200",
    [string]$Username = "elastic",
    [string]$Password = "muVmg+YxSgExd2NKBttV"
)

Write-Host "Создание бэкапа Elasticsearch индекса..." -ForegroundColor Green

# Создаем директорию для бэкапа если её нет
if (!(Test-Path $BackupPath)) {
    New-Item -ItemType Directory -Path $BackupPath -Force
    Write-Host "Создана директория: $BackupPath"
}

# Кодируем credentials для Basic Auth
$base64AuthInfo = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes(("${Username}:${Password}")))
$headers = @{
    Authorization = "Basic $base64AuthInfo"
    "Content-Type" = "application/json"
}

try {
    # Проверяем подключение к Elasticsearch
    Write-Host "Проверка подключения к Elasticsearch..."
    $healthResponse = Invoke-RestMethod -Uri "$ElasticsearchUrl/_cluster/health" -Headers $headers -Method Get
    Write-Host "Статус кластера: $($healthResponse.status)" -ForegroundColor Yellow

    # Получаем список индексов
    Write-Host "Получение списка индексов..."
    $indices = Invoke-RestMethod -Uri "$ElasticsearchUrl/_cat/indices?format=json" -Headers $headers -Method Get
    
    foreach ($index in $indices) {
        $indexName = $index.index
        
        # Пропускаем системные индексы
        if ($indexName.StartsWith(".")) {
            continue
        }
        
        Write-Host "Экспорт индекса: $indexName" -ForegroundColor Cyan
        
        # Экспортируем маппинги индекса
        $mappings = Invoke-RestMethod -Uri "$ElasticsearchUrl/$indexName/_mapping" -Headers $headers -Method Get
        $mappings | ConvertTo-Json -Depth 10 | Out-File -FilePath "$BackupPath/${indexName}_mapping.json" -Encoding UTF8
        
        # Экспортируем настройки индекса
        $settings = Invoke-RestMethod -Uri "$ElasticsearchUrl/$indexName/_settings" -Headers $headers -Method Get
        $settings | ConvertTo-Json -Depth 10 | Out-File -FilePath "$BackupPath/${indexName}_settings.json" -Encoding UTF8
        
        # Экспортируем данные индекса
        $searchBody = @{
            query = @{
                match_all = @{}
            }
            size = 1000
        } | ConvertTo-Json -Depth 3
        
        $scrollResponse = Invoke-RestMethod -Uri "$ElasticsearchUrl/$indexName/_search?scroll=5m" -Headers $headers -Method Post -Body $searchBody
        
        $allDocuments = @()
        $allDocuments += $scrollResponse.hits.hits
        
        # Продолжаем скроллинг для получения всех документов
        while ($scrollResponse.hits.hits.Count -gt 0) {
            $scrollId = $scrollResponse._scroll_id
            $scrollBody = @{
                scroll = "5m"
                scroll_id = $scrollId
            } | ConvertTo-Json
            
            $scrollResponse = Invoke-RestMethod -Uri "$ElasticsearchUrl/_search/scroll" -Headers $headers -Method Post -Body $scrollBody
            
            if ($scrollResponse.hits.hits.Count -gt 0) {
                $allDocuments += $scrollResponse.hits.hits
            }
        }
        
        # Сохраняем документы
        $allDocuments | ConvertTo-Json -Depth 10 | Out-File -FilePath "$BackupPath/${indexName}_data.json" -Encoding UTF8
        
        Write-Host "Экспортировано документов: $($allDocuments.Count)" -ForegroundColor Green
    }
    
    # Создаем метаданные бэкапа
    $backupMetadata = @{
        timestamp = Get-Date -Format "yyyy-MM-ddTHH:mm:ssZ"
        elasticsearch_version = $healthResponse.version.number
        cluster_name = $healthResponse.cluster_name
        indices_count = ($indices | Where-Object { -not $_.index.StartsWith(".") }).Count
        total_documents = ($indices | Where-Object { -not $_.index.StartsWith(".") } | Measure-Object -Property "docs.count" -Sum).Sum
    }
    
    $backupMetadata | ConvertTo-Json -Depth 3 | Out-File -FilePath "$BackupPath/backup_metadata.json" -Encoding UTF8
    
    Write-Host "`nБэкап успешно создан в: $BackupPath" -ForegroundColor Green
    Write-Host "Файлы бэкапа:" -ForegroundColor Yellow
    Get-ChildItem $BackupPath | ForEach-Object { Write-Host "  - $($_.Name)" }
    
} catch {
    Write-Host "Ошибка при создании бэкапа: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}

