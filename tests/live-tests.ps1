# Live tests against a running TIA Portal with a project open.
#
#   .\tests\live-tests.ps1                 read-only checks (changes nothing)
#   .\tests\live-tests.ps1 -Write          also creates, edits and deletes TEMPORARY objects
#   .\tests\live-tests.ps1 -S7             also reads live values from the PLC
#
# The -Write part only touches objects it creates itself, all named MCPTEST_*, and
# removes them again. It never saves the project: if anything looks wrong afterwards,
# close the project in TIA Portal WITHOUT saving and nothing has happened.
# Still, run it on a copy of a project you care about.
#
# On the first run of a new build, TIA Portal shows an "Openness access" prompt:
# answer "Yes to all" in the TIA Portal window.

param(
    [string] $Exe = "$PSScriptRoot\..\bin\Release\TiaMcpServer.exe",
    [switch] $Write,
    [switch] $S7,
    [string] $PlcIp,
    [switch] $ShowResults,
    [string[]] $ServerArgs = @()
)

$ErrorActionPreference = 'Stop'
Import-Module "$PSScriptRoot\McpClient.psm1" -Force

$script:Passed = 0
$script:Failed = 0
$script:Skipped = 0

function Check([string] $Name, [bool] $Condition, [string] $Detail = '') {
    if ($Condition) { $script:Passed++; Write-Host "  ok    $Name" }
    else { $script:Failed++; Write-Host "  FAIL  $Name  $Detail" -ForegroundColor Red }
}
function Skip([string] $Name, [string] $Why) { $script:Skipped++; Write-Host "  skip  $Name  ($Why)" }
function Section([string] $Title) { Write-Host "`n== $Title" }

# Calls a tool; -Expect ok|error states what a pass looks like.
function Tool([string] $Name, [hashtable] $Arguments = @{}, [string] $Expect = 'ok', [string] $Match, [int] $TimeoutSec = 300) {
    $r = Invoke-McpTool $script:Server $Name $Arguments -TimeoutSec $TimeoutSec
    $ok = if ($Expect -eq 'error') { $r.IsError } else { -not $r.IsError }
    if ($ok -and $Match) { $ok = $r.Text -match $Match }
    $label = if ($Expect -eq 'error') { "$Name is refused" } else { $Name }
    $detail = if ($ok) { '' } else { ($r.Text -replace '\s+', ' ') }
    if ($detail.Length -gt 400) { $detail = $detail.Substring(0, 400) + '...' }
    Check $label $ok $detail
    if ($ShowResults) {
        $text = $r.Text -replace '\s+', ' '
        if ($text.Length -gt 700) { $text = $text.Substring(0, 700) + '...' }
        Write-Host "        $text" -ForegroundColor DarkGray
    }
    return $r
}

# Deletes every MCPTEST_* object. Quiet: used before the write tests and as a safety net after them.
function Remove-TestObjects {
    function Quiet([string] $Name, [hashtable] $Arguments) {
        $Arguments.confirm = $true
        return Invoke-McpTool $script:Server $Name $Arguments -TimeoutSec 300
    }
    $removed = 0
    $blocks = (Quiet 'tia_list_blocks' @{}).Data.blocks | Where-Object { $_.name -like 'MCPTEST*' }
    # Callers before callees, instance DBs before their FB: two passes cover the order.
    foreach ($pass in 1, 2) {
        foreach ($b in $blocks) { if (-not (Quiet 'tia_delete_block' @{ block = $b.name }).IsError) { $removed++ } }
    }
    foreach ($t in (Quiet 'tia_list_types' @{}).Data.types | Where-Object { $_.name -like 'MCPTEST*' }) {
        if (-not (Quiet 'tia_delete_type' @{ type = $t.name }).IsError) { $removed++ }
    }
    foreach ($t in (Quiet 'tia_list_plc_tags' @{}).Data.tagTables | Where-Object { $_.table -like 'MCPTEST*' }) {
        if (-not (Quiet 'tia_delete_tag_table' @{ table = $t.table }).IsError) { $removed++ }
    }
    foreach ($w in (Quiet 'tia_list_watchtables' @{}).Data.watchTables | Where-Object { $_.name -like 'MCPTEST*' }) {
        if (-not (Quiet 'tia_delete_watchtable' @{ name = $w.name }).IsError) { $removed++ }
    }
    foreach ($g in 'MCPTEST/Sub', 'MCPTEST') { $null = Quiet 'tia_delete_block_group' @{ path = $g } }

    $alarms = (Quiet 'tia_hmi_list_alarms' @{}).Data
    foreach ($a in @($alarms.discreteAlarms) + @($alarms.analogAlarms) | Where-Object { $_.name -like 'MCPTEST*' }) {
        if (-not (Quiet 'tia_hmi_delete_alarm' @{ name = $a.name }).IsError) { $removed++ }
    }
    foreach ($s in (Quiet 'tia_list_hmi_screens' @{}).Data.screens | Where-Object { $_.name -like 'MCPTEST*' }) {
        if (-not (Quiet 'tia_hmi_delete_screen' @{ screen = $s.name; removeEmptyGroups = $true }).IsError) { $removed++ }
    }
    foreach ($t in (Quiet 'tia_hmi_list_tags' @{ filter = 'MCPTEST' }).Data.tags | Where-Object { $_.name -like 'MCPTEST*' }) {
        if (-not (Quiet 'tia_hmi_delete_tag' @{ tag = $t.name }).IsError) { $removed++ }
    }
    if ($removed -gt 0) { Write-Host "        (removed $removed leftover MCPTEST object(s))" }
}

$tmp = Join-Path ([IO.Path]::GetTempPath()) ("tia-mcp-live-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmp | Out-Null
$script:Server = Start-McpServer -Exe $Exe -Arguments $ServerArgs

try {
    # ================================================================== connect
    Section 'Connection'
    $doc = Tool 'tia_doctor'
    Write-Host '        (if this waits: answer the Openness prompt in TIA Portal with "Yes to all")'
    $conn = Tool 'tia_connect' @{ mode = 'attach' } -TimeoutSec 600
    if ($conn.IsError -or -not $conn.Data.projectOpen) { throw "Not connected to a project: $($conn.Text)" }
    Write-Host "        project: $($conn.Data.projectName)"
    $info = Tool 'tia_project_info'
    $culture = $info.Data.languages.editing
    $before = (Tool 'tia_state').Data.unsavedChanges

    # ================================================================== read-only
    Section 'Devices and hardware (read-only)'
    $devices = (Tool 'tia_list_devices').Data.devices
    $plc = ($devices | ForEach-Object { $_.software } | Where-Object { $_.kind -eq 'plc' } | Select-Object -First 1).name
    $hmi = ($devices | ForEach-Object { $_.software } | Where-Object { $_.kind -eq 'hmiUnified' } | Select-Object -First 1).name
    Write-Host "        plc: $plc   unified hmi: $hmi"
    $null = Tool 'tia_list_ip_addresses'
    $mods = Tool 'tia_list_modules'
    $null = Tool 'tia_find_hardware_catalog_entries' @{ filter = '6ES7 214' }
    if ($plc) {
        $null = Tool 'tia_online_state'
        $null = Tool 'tia_dump_plc_protection'
        $null = Tool 'tia_debug_device_tree' @{ device = $devices[0].name }
    }

    if ($plc) {
        Section 'PLC program (read-only)'
        $blocks = (Tool 'tia_list_blocks').Data.blocks
        $scl = $blocks | Where-Object { $_.language -eq 'SCL' -and $_.type -in 'FB', 'FC' } | Select-Object -First 1
        $db = $blocks | Where-Object { $_.type -eq 'GlobalDB' } | Select-Object -First 1
        $lad = $blocks | Where-Object { $_.language -in 'LAD', 'FBD' } | Select-Object -First 1

        if ($scl) {
            $src = Tool 'tia_read_block' @{ block = $scl.name }
            Check "  $($scl.name) is returned as source text" ($src.Data.format -eq 'source' -and $src.Data.content -match 'END_FUNCTION')
            $null = Tool 'tia_get_block_interface' @{ block = $scl.name }
            $exp = Tool 'tia_export_source' @{ block = $scl.name; outDir = $tmp }
            Check '  the exported source file exists' ($exp.Data.path -and (Test-Path $exp.Data.path))
            $null = Tool 'tia_get_cross_references' @{ name = $scl.name; maxLocations = 20 }
        } else { Skip 'SCL block reads' 'no SCL FB/FC in the project' }

        if ($db) {
            $dbSrc = Tool 'tia_read_block' @{ block = $db.name }
            Check "  $($db.name) (DB) is returned as source text" ($dbSrc.Data.format -eq 'source' -and $dbSrc.Data.content -match 'DATA_BLOCK')
            $null = Tool 'tia_get_block_interface' @{ block = $db.name }
        } else { Skip 'DB reads' 'no global DB in the project' }

        if ($lad) {
            $ladXml = Tool 'tia_read_block' @{ block = $lad.name; maxChars = 4000 }
            Check "  $($lad.name) ($($lad.language)) falls back to XML" ($ladXml.Data.format -eq 'xml')
            $xmlExp = Tool 'tia_export_block' @{ block = $lad.name; outDir = $tmp }
        } else { Skip 'graphical block read' 'no LAD/FBD block in the project' }

        $null = Tool 'tia_list_types'
        $tags = Tool 'tia_list_plc_tags'
        $firstTag = $tags.Data.tagTables | ForEach-Object { $_.tags } | Select-Object -First 1
        if ($firstTag) { $null = Tool 'tia_get_cross_references' @{ name = $firstTag.name; maxLocations = 20 } }
        $null = Tool 'tia_list_watchtables'
        $null = Tool 'tia_read_block' @{ block = 'MCPTEST_NoSuchBlock' } -Expect error -Match 'tia_list_blocks'
    }

    if ($hmi) {
        Section 'WinCC Unified (read-only)'
        $screens = (Tool 'tia_list_hmi_screens').Data.screens
        $null = Tool 'tia_hmi_list_tags'
        $null = Tool 'tia_hmi_list_alarms'
        $classes = Tool 'tia_hmi_get_software_object' @{ path = 'AlarmClasses' }
        Check '  alarm classes are listed as a collection' ($classes.Data.count -gt 0) ($classes.Text -replace '\s+', ' ')
        $null = Tool 'tia_hmi_get_software_object' @{ path = 'NoSuchPart' } -Expect error -Match 'no readable part'
        if ($screens) {
            $screenName = ($screens | Sort-Object items -Descending | Select-Object -First 1).name
            $screen = Tool 'tia_hmi_get_screen' @{ screen = $screenName }
            $null = Tool 'tia_hmi_get_item' @{ screen = $screenName; item = '@screen' }
            $firstItem = $screen.Data.items | Select-Object -First 1
            if ($firstItem) { $null = Tool 'tia_hmi_get_item' @{ screen = $screenName; item = $firstItem.name } }
            $null = Tool 'tia_hmi_find_tag_usage'
            $null = Tool 'tia_hmi_validate' @{ screen = $screenName }
            $dry = Tool 'tia_hmi_replace_tag_refs' @{ pattern = '^(.+)$'; replacement = 'X_$1' }
            Check '  a dry run changes nothing' ($dry.Data.dryRun -eq $true -and $dry.Data.applied -eq $false)
            $null = Tool 'tia_dump_screen_item_bounds' @{ screen = $screenName }
            $null = Tool 'tia_debug_screen_item_creation_infos' @{ screen = $screenName }
            $null = Tool 'tia_debug_screen_item_type_properties' @{ screen = $screenName; typeName = 'HmiIOField' }
        }
        $null = Tool 'tia_debug_reflect_type' @{ typeName = 'HmiFillPattern' }
        $null = Tool 'tia_debug_search_types' @{ pattern = 'TextList' }
    } else { Skip 'WinCC Unified checks' 'no Unified HMI in the project' }

    Section 'Project archive (read-only)'
    if ($before -eq $false) {
        $arch = Tool 'tia_archive_project' @{ targetDir = $tmp; name = 'mcptest_archive' } -TimeoutSec 900
        Check '  the archive file exists' ($arch.Data.path -and (Test-Path $arch.Data.path)) "$($arch.Data.path)"
    } else { Skip 'tia_archive_project' 'the project has unsaved changes; TIA only archives a saved project' }

    $after = (Tool 'tia_state').Data.unsavedChanges
    Check 'the read-only pass left the project untouched' ($before -eq $after) "unsavedChanges $before -> $after"

    # ================================================================== live S7
    if ($S7) {
        Section 'Live S7 (read-only on the PLC)'
        $ipArg = @{}
        if ($PlcIp) { $ipArg.ip = $PlcIp }
        $cpu = Tool 'tia_s7_cpu_info' $ipArg
        if (-not $cpu.IsError) {
            Write-Host "        cpu: $($cpu.Data.status)  $($cpu.Data.orderCode)  $($cpu.Data.moduleName)"
            $null = Tool 'tia_s7_read' @{ area = 'M'; start = 0; size = 4 }
            $vals = Tool 'tia_s7_read_values' @{ items = @(
                @{ address = 'M0.0' }, @{ address = 'MB0' }, @{ address = 'MW0'; type = 'Int' },
                @{ address = 'MD0'; type = 'Real' }, @{ address = 'IB0' }, @{ address = 'QB0' }, 'nonsense') }
            Check '  a bad address fails alone' (@($vals.Data.values | Where-Object { $_.error }).Count -eq 1)
            if ($firstTag) {
                $names = @($tags.Data.tagTables | ForEach-Object { $_.tags } | Where-Object { $_.address } | Select-Object -First 5 | ForEach-Object { $_.name })
                $null = Tool 'tia_s7_read_tags' @{ tags = $names + 'MCPTEST_NoSuchTag' }
            }
        }
    }

    # ================================================================== write
    if (-not $Write) {
        Write-Host "`n(write tests not run; pass -Write to create/modify/delete temporary MCPTEST_* objects)"
    }
    else {
        # A previous run that died half-way may have left MCPTEST_* objects behind.
        Remove-TestObjects
    }

    if ($Write -and $plc) {
        Section 'PLC: blocks (temporary objects)'
        $null = Tool 'tia_create_block_group' @{ path = 'MCPTEST/Sub' }

        $fc = @'
FUNCTION "MCPTEST_FC" : Int
{ S7_Optimized_Access := 'TRUE' }
   VAR_INPUT
      a : Int;   // first operand
      b : Int;
   END_VAR
BEGIN
   #MCPTEST_FC := #a + #b;
END_FUNCTION
'@
        $c1 = Tool 'tia_import_scl' @{ source = $fc; name = 'mcptest_fc'; group = 'MCPTEST/Sub' }
        Check '  the new FC is reported as created' ($c1.Data.created -contains 'MCPTEST_FC')
        Check '  it was compiled on import, without errors' ($c1.Data.compiled -and $c1.Data.errorCount -eq 0) ($c1.Text -replace '\s+', ' ')
        $again = Tool 'tia_import_scl' @{ source = $fc } -Expect error -Match 'Confirmation required'
        Check '  the refusal names the block' ($again.Text -match 'MCPTEST_FC')
        $c2 = Tool 'tia_import_scl' @{ source = ($fc -replace '#a \+ #b', '#a - #b'); confirm = $true }
        Check '  the replacement is backed up' ($c2.Data.replaced -contains 'MCPTEST_FC' -and (Test-Path $c2.Data.backups.MCPTEST_FC)) "$($c2.Data.backups)"
        if ($c2.Data.backups.MCPTEST_FC -and (Test-Path $c2.Data.backups.MCPTEST_FC)) {
            Check '  the backup holds the OLD code' ((Get-Content $c2.Data.backups.MCPTEST_FC -Raw) -match '#a \+ #b')
        }
        $rd = Tool 'tia_read_block' @{ block = 'MCPTEST_FC' }
        Check '  the block now holds the NEW code' ($rd.Data.content -match '#a - #b')
        $where = (Tool 'tia_list_blocks').Data.blocks | Where-Object name -eq 'MCPTEST_FC'
        Check '  it stayed in its folder' ($where.group -eq 'MCPTEST/Sub') $where.group
        $cmp = Tool 'tia_compile' @{ block = 'MCPTEST_FC' }
        Check '  it compiles without errors' ($cmp.Data.errorCount -eq 0) ($cmp.Text -replace '\s+', ' ')
        # XML round trip: export the compiled block, then import it over itself.
        $xmlOut = Tool 'tia_export_block' @{ block = 'MCPTEST_FC'; outDir = $tmp }
        $null = Tool 'tia_import_block' @{ xmlPath = $xmlOut.Data.path } -Expect error -Match 'already exists'
        $xmlIn = Tool 'tia_import_block' @{ xml = [IO.File]::ReadAllText($xmlOut.Data.path); overwrite = $true; confirm = $true }
        Check '  inline XML replaces the block, with a backup' ($xmlIn.Data.replacedExisting -and (Test-Path $xmlIn.Data.backup)) ($xmlIn.Text -replace '\s+', ' ')
        $null = Tool 'tia_compile' @{ block = 'MCPTEST_FC' }

        $itf = Tool 'tia_get_block_interface' @{ block = 'MCPTEST_FC' }
        $inputs = ($itf.Data.sections | Where-Object section -eq 'Input').members
        Check '  the interface lists inputs and comments' ($inputs.Count -eq 2 -and $inputs[0].comment -match 'first operand') ($itf.Text -replace '\s+', ' ')

        # TIA generates a block even from broken source; the import must surface the compiler errors.
        $bad = Tool 'tia_import_scl' @{ source = "FUNCTION `"MCPTEST_Bad`" : Void`nBEGIN`n   #undeclared := 1;`nEND_FUNCTION" }
        Check '  broken source comes back with compiler errors' ($bad.Data.errorCount -gt 0 -and $bad.Data.next -match 'Fix the errors') ($bad.Text -replace '\s+', ' ')
        $null = Tool 'tia_delete_block' @{ block = 'MCPTEST_Bad'; confirm = $true }

        $fb = @'
FUNCTION_BLOCK "MCPTEST_FB"
{ S7_Optimized_Access := 'TRUE' }
   VAR_INPUT
      enable : Bool;
   END_VAR
   VAR_OUTPUT
      count : Int;
   END_VAR
BEGIN
   IF #enable THEN
      #count := "MCPTEST_FC"(a := #count, b := 1);
   END_IF;
END_FUNCTION_BLOCK
'@
        $null = Tool 'tia_import_scl' @{ source = $fb; group = 'MCPTEST' }
        $idb = Tool 'tia_create_instance_db' @{ name = 'MCPTEST_FB_DB'; instanceOf = 'MCPTEST_FB'; group = 'MCPTEST' }
        Check '  the instance DB points at its FB' ($idb.Data.instanceOf -eq 'MCPTEST_FB')
        $null = Tool 'tia_create_instance_db' @{ name = 'MCPTEST_FB_DB'; instanceOf = 'MCPTEST_FB' } -Expect error -Match 'already exists'
        $xr = Tool 'tia_get_cross_references' @{ name = 'MCPTEST_FC' }
        Check '  cross-references find the caller' ($xr.Text -match 'MCPTEST_FB')

        Section 'PLC: LAD block'
        $ladArgs = @{
            name = 'MCPTEST_LAD'; type = 'FB'; group = 'MCPTEST'
            interface = @{
                input  = @(@{ name = 'Start'; datatype = 'Bool' }, @{ name = 'Stop'; datatype = 'Bool' }, @{ name = 'Level'; datatype = 'Int' })
                output = @(@{ name = 'Motor'; datatype = 'Bool' }, @{ name = 'Done'; datatype = 'Bool' })
                static = @(@{ name = 'Tmr'; datatype = 'TON_TIME' }, @{ name = 'Total'; datatype = 'Int' })
            }
            networks = @(
                @{ elements = @(
                       @{ type = 'branch'; branches = @(, @(@{ type = 'contact'; operand = '#Start' })) + @(, @(@{ type = 'contact'; operand = '#Motor' })) },
                       @{ type = 'contact'; operand = '#Stop'; negated = $true })
                   outputs = @(@{ type = 'coil'; operand = '#Motor' }) },
                @{ elements = @(@{ type = 'contact'; operand = '#Motor' }, @{ type = 'ton'; instance = '#Tmr'; pt = 'T#5s' })
                   outputs = @(@{ type = 'coil'; operand = '#Done' }) },
                @{ elements = @(@{ type = 'gt'; operand = '#Level'; operand2 = '100'; dataType = 'Int' },
                               @{ type = 'move'; source = '#Level'; operand = '#Total' }) }
            )
        }
        $ladRes = Tool 'tia_create_lad_block' $ladArgs
        Check '  the LAD block compiles without errors' ($ladRes.Data.compile.errorCount -eq 0) ($ladRes.Text -replace '\s+', ' ')
        $ladBack = Tool 'tia_read_block' @{ block = 'MCPTEST_LAD'; maxChars = 0 }
        Check '  TIA stores it as LAD' ($ladBack.Data.language -eq 'LAD' -and $ladBack.Data.format -eq 'xml')
        $null = Tool 'tia_create_lad_block' $ladArgs -Expect error -Match 'already exists'
        if ($culture) {
            $titled = $ladArgs.Clone(); $titled.overwrite = $true; $titled.confirm = $true; $titled.culture = $culture
            $titled.networks[0].title = 'Seal-in'
            $t = Tool 'tia_create_lad_block' $titled
            Check '  network titles import in the project language' ($t.Data.compile.errorCount -eq 0)
        }

        Section 'PLC: data types'
        $udt = "TYPE `"MCPTEST_UDT`"`n   STRUCT`n      speed : Real;   // m/s`n      running : Bool;`n   END_STRUCT;`nEND_TYPE"
        $null = Tool 'tia_import_scl' @{ source = $udt }
        $types = Tool 'tia_list_types'
        Check '  the data type is listed' (@($types.Data.types | Where-Object name -eq 'MCPTEST_UDT').Count -eq 1)
        $udtSrc = Tool 'tia_read_block' @{ block = 'MCPTEST_UDT' }
        Check '  it reads back as TYPE source' ($udtSrc.Data.content -match 'TYPE "MCPTEST_UDT"')
        $null = Tool 'tia_delete_type' @{ type = 'MCPTEST_UDT' } -Expect error -Match 'Confirmation required'
        $delType = Tool 'tia_delete_type' @{ type = 'MCPTEST_UDT'; confirm = $true }
        Check '  its backup exists' ($delType.Data.backup -and (Test-Path $delType.Data.backup)) "$($delType.Data.backup)"

        Section 'PLC: tags'
        $null = Tool 'tia_create_tag_table' @{ name = 'MCPTEST_Tags' }
        $mk = Tool 'tia_create_plc_tags' @{ table = 'MCPTEST_Tags'; tags = @(
            @{ name = 'MCPTEST_Bit'; dataType = 'Bool'; address = '%M4000.0'; comment = 'test bit' },
            @{ name = 'MCPTEST_Word'; dataType = 'Int'; address = '%MW4002' },
            @{ name = 'MCPTEST_Bit'; dataType = 'Bool'; address = '%M4000.1' }) }
        $statuses = $mk.Data.results | ForEach-Object { $_.status }
        Check '  two created, the duplicate skipped' (($statuses -join ',') -eq 'created,created,skipped') ($statuses -join ',')
        $null = Tool 'tia_create_plc_constants' @{ table = 'MCPTEST_Tags'; constants = @(@{ name = 'MCPTEST_MAX'; dataType = 'Int'; value = '42' }) }
        $up = Tool 'tia_update_plc_tag' @{ tag = 'MCPTEST_Word'; newName = 'MCPTEST_Count'; comment = 'renamed' }
        Check '  the update returns before and after' ($up.Data.before.name -eq 'MCPTEST_Word' -and $up.Data.after.name -eq 'MCPTEST_Count')
        $lt = Tool 'tia_list_plc_tags' @{ table = 'MCPTEST_Tags' }
        Check '  tags, comments and constants are listed' (($lt.Text -match 'test bit') -and ($lt.Text -match 'MCPTEST_MAX') -and ($lt.Text -match 'MCPTEST_Count'))
        $xt = Tool 'tia_export_tag_table' @{ table = 'MCPTEST_Tags'; outDir = $tmp }
        $null = Tool 'tia_import_tag_table' @{ xmlPath = $xt.Data.path } -Expect error -Match 'already exists'
        $null = Tool 'tia_import_tag_table' @{ xmlPath = $xt.Data.path; overwrite = $true; confirm = $true }
        $null = Tool 'tia_delete_plc_tag' @{ tag = 'MCPTEST_Bit'; confirm = $true }
        $null = Tool 'tia_delete_tag_table' @{ table = 'MCPTEST_Tags'; confirm = $true }

        Section 'PLC: watch tables'
        $null = Tool 'tia_create_watchtable' @{ name = 'MCPTEST_Watch' }
        $wx = Tool 'tia_export_watchtable' @{ name = 'MCPTEST_Watch'; outDir = $tmp }
        $null = Tool 'tia_import_watchtable' @{ xmlPath = $wx.Data.path; overwrite = $true; confirm = $true }
        $null = Tool 'tia_list_watchtables' -Match 'MCPTEST_Watch'
        $null = Tool 'tia_delete_watchtable' @{ name = 'MCPTEST_Watch'; confirm = $true }

        Section 'PLC: cleanup'
        $null = Tool 'tia_delete_block_group' @{ path = 'MCPTEST'; confirm = $true } -Expect error -Match 'not empty'
        foreach ($b in 'MCPTEST_LAD', 'MCPTEST_FB_DB', 'MCPTEST_FB', 'MCPTEST_FC') {
            $null = Tool 'tia_delete_block' @{ block = $b; confirm = $true }
        }
        $null = Tool 'tia_delete_block_group' @{ path = 'MCPTEST/Sub'; confirm = $true }
        $null = Tool 'tia_delete_block_group' @{ path = 'MCPTEST'; confirm = $true }
        $left = @((Tool 'tia_list_blocks').Data.blocks | Where-Object { $_.name -like 'MCPTEST*' })
        Check '  no MCPTEST block is left' ($left.Count -eq 0) ($left.name -join ', ')
    }

    if ($Write -and $hmi) {
        Section 'HMI: tags and alarms (temporary objects)'
        $ht = Tool 'tia_hmi_create_tags' @{ tags = @(
            @{ name = 'MCPTEST_Flag'; dataType = 'Bool' },
            @{ name = 'MCPTEST_Value'; dataType = 'Int' },
            @{ name = 'MCPTEST_Broken'; connection = 'NoSuchConnection'; plcTag = 'x' }) }
        $hs = $ht.Data.results | ForEach-Object { $_.status }
        Check '  two internal tags created, the bad one reported' (($hs -join ',') -eq 'created,created,error') ($ht.Text -replace '\s+', ' ')
        $null = Tool 'tia_hmi_list_tags' @{ filter = 'MCPTEST' } -Match 'MCPTEST_Value'

        # A PLC-linked tag, pointed at whatever an existing HMI tag already reads.
        $linked = $null
        $model = (Invoke-McpTool $script:Server 'tia_hmi_list_tags').Data.tags | Where-Object { $_.plcTag -and $_.connection } | Select-Object -First 1
        if ($model) {
            $linked = Tool 'tia_hmi_create_tag' @{ name = 'MCPTEST_Linked'; connection = $model.connection; plcTag = $model.plcTag }
            Check '  the linked tag takes its type from the PLC tag' ($linked.Data.kind -eq 'plc' -and $linked.Data.dataType -eq $model.dataType) ($linked.Text -replace '\s+', ' ')
        } else { Skip 'PLC-linked HMI tag' 'no existing PLC-linked HMI tag to copy the address from' }
        $al = Tool 'tia_hmi_create_alarm' @{ name = 'MCPTEST_Alarm'; tag = 'MCPTEST_Value'; bit = 3; text = 'Test alarm <1>' }
        Check '  the alarm keeps tag, bit and text' ($al.Data.tag -eq 'MCPTEST_Value' -and $al.Data.bit -eq 3 -and $al.Data.text -eq 'Test alarm <1>') ($al.Text -replace '\s+', ' ')
        $an = Tool 'tia_hmi_create_alarm' @{ kind = 'analog'; name = 'MCPTEST_High'; tag = 'MCPTEST_Value'; condition = 'UpperLimit'; conditionValue = '90'; text = 'Too high' }
        $null = Tool 'tia_hmi_list_alarms' -Match 'MCPTEST_High'
        $null = Tool 'tia_hmi_delete_alarm' @{ name = 'MCPTEST_Alarm'; confirm = $true }
        $null = Tool 'tia_hmi_delete_alarm' @{ name = 'MCPTEST_High'; confirm = $true }

        Section 'HMI: screen and items'
        $null = Tool 'tia_hmi_create_screen' @{ name = 'MCPTEST_Screen'; width = 800; height = 480 }
        $batch = Tool 'tia_hmi_create_items' @{ screen = 'MCPTEST_Screen'; items = @(
            @{ type = 'HmiText'; name = 'Title'; left = 20; top = 10; width = 400; height = 40; text = 'Test & <screen>'
               properties = @{ 'Font.Size' = 24; ForeColor = '#1E90FF' } },
            @{ type = 'HmiButton'; name = 'BtnStart'; left = 20; top = 70; width = 160; height = 60; text = 'Start'
               tagBindings = @(@{ property = 'BackColor'; tag = 'MCPTEST_Flag'; falseColor = 'LightGray'; trueColor = '#00C000' })
               events = @(@{ event = 'Down'; script = 'HMIRuntime.Tags.SysFct.SetBitInTag("MCPTEST_Flag", 0);' },
                          @{ event = 'Up'; script = 'HMIRuntime.Tags.SysFct.ResetBitInTag("MCPTEST_Flag", 0);' }) },
            @{ type = 'IOField'; name = 'FldValue'; left = 200; top = 70; width = 120; height = 60
               properties = @{ IOFieldType = 'Output' }
               tagBindings = @(@{ property = 'ProcessValue'; tag = 'MCPTEST_Value'; readOnly = $true }) },
            @{ type = 'HmiRectangle'; name = 'Box'; left = 340; top = 70; width = 60; height = 60 }) }
        Check '  four items created' (@($batch.Data.results | Where-Object status -eq 'created').Count -eq 4) ($batch.Text -replace '\s+', ' ')

        $rolled = Tool 'tia_hmi_create_items' @{ screen = 'MCPTEST_Screen'; items = @(
            @{ type = 'HmiButton'; name = 'ShouldNotStay'; left = 0; top = 300 },
            @{ type = 'HmiButton'; name = 'Broken'; properties = @{ NoSuchProperty = 1 } }) } -Expect error -Match 'NoSuchProperty'
        $scr = Tool 'tia_hmi_get_screen' @{ screen = 'MCPTEST_Screen' }
        Check '  a failing batch leaves nothing behind' (@($scr.Data.items | Where-Object name -in 'ShouldNotStay', 'Broken').Count -eq 0) (($scr.Data.items | ForEach-Object name) -join ', ')
        $title = $scr.Data.items | Where-Object name -eq 'Title'
        Check '  text round-trips as plain text' ($title.text -eq 'Test & <screen>') $title.text
        $btn = $scr.Data.items | Where-Object name -eq 'BtnStart'
        Check '  the button has its binding and two scripts' ($btn.dynamizations.Count -eq 1 -and $btn.events.Count -eq 2) ($btn | ConvertTo-Json -Depth 6 -Compress)

        $item = Tool 'tia_hmi_get_item' @{ screen = 'MCPTEST_Screen'; item = 'Title' }
        Check '  nested properties are read' ($item.Data.properties.Font.Size -eq 24 -and $item.Data.properties.ForeColor -eq '#1E90FF') ($item.Text -replace '\s+', ' ')
        $set = Tool 'tia_hmi_set_widget_property' @{ screen = 'MCPTEST_Screen'; item = 'Title'; property = 'Font.Weight'; value = 'Bold' }
        Check '  a property change is read back and returns the previous value' ($set.Data.set -eq $true -and $set.Data.value -eq 'Bold' -and $null -ne $set.Data.previous) ($set.Text -replace '\s+', ' ')
        $null = Tool 'tia_hmi_set_widget_property' @{ screen = 'MCPTEST_Screen'; item = 'Title'; property = 'Font.Weight'; value = 'Heavyish' } -Expect error -Match 'Valid values'
        if ($culture) {
            $one = Tool 'tia_hmi_set_widget_property' @{ screen = 'MCPTEST_Screen'; item = 'Title'; property = "Text@$culture"; value = 'One language' }
            Check '  a text is written for one language' ($one.Data.set -eq $true -and $one.Data.value -eq 'One language') ($one.Text -replace '\s+', ' ')
        }
        $null = Tool 'tia_hmi_set_widget_property' @{ screen = 'MCPTEST_Screen'; item = 'Title'; property = 'Text@xx-XX'; value = 'x' } -Expect error -Match 'is not in the project'
        $null = Tool 'tia_hmi_set_widget_property' @{ screen = 'MCPTEST_Screen'; item = 'Title'; property = 'Font.Weight@en-US'; value = 'Bold' } -Expect error -Match 'not a multilingual text'
        $null = Tool 'tia_hmi_set_widget_property' @{ screen = 'MCPTEST_Screen'; item = 'Title'; property = 'Font[0].Size'; value = '12' } -Expect error -Match 'not a collection'
        $null = Tool 'tia_hmi_set_widget_color' @{ screen = 'MCPTEST_Screen'; item = 'Box'; property = 'BackColor'; color = 'NotAColour' } -Expect error -Match 'Unknown colour'
        $null = Tool 'tia_hmi_set_widget_color' @{ screen = 'MCPTEST_Screen'; item = 'Box'; property = 'BackColor'; color = '255,128,0' }
        $mv = Tool 'tia_hmi_set_item_bounds' @{ screen = 'MCPTEST_Screen'; item = 'Title'; left = 40 }
        Check '  a shape can be moved, other bounds kept' ($mv.Data.after.left -eq 40 -and $mv.Data.after.width -eq 400) ($mv.Text -replace '\s+', ' ')
        $null = Tool 'tia_hmi_set_button_text' @{ screen = 'MCPTEST_Screen'; button = 'BtnStart'; text = 'Go' }
        $null = Tool 'tia_hmi_set_screen_background' @{ screen = 'MCPTEST_Screen'; backColor = '#102030'; alternateColor = 'Black'; fillPattern = 'GradientVertical' }
        $null = Tool 'tia_hmi_set_widget_property' @{ screen = 'MCPTEST_Screen'; item = '@screen'; property = 'BackColor'; value = 'White' }

        Section 'HMI: scripts and bindings'
        $ev = Tool 'tia_hmi_set_event_script' @{ screen = 'MCPTEST_Screen'; item = '@screen'; eventType = 'Loaded'; scriptCode = 'HMIRuntime.Trace("loaded");' }
        Check '  a screen event script passes the syntax check' ($ev.Data.errors.Count -eq 0) ($ev.Text -replace '\s+', ' ')
        $syn = Tool 'tia_hmi_set_event_script' @{ screen = 'MCPTEST_Screen'; item = 'BtnStart'; eventType = 'Tapped'; scriptCode = 'this is ( not javascript' }
        Check '  a broken script is reported by the syntax check' ($syn.Data.errors.Count -gt 0) ($syn.Text -replace '\s+', ' ')
        $null = Tool 'tia_hmi_set_event_script' @{ screen = 'MCPTEST_Screen'; item = 'BtnStart'; eventType = 'Wobbled'; scriptCode = 'x' } -Expect error -Match 'Valid events'
        $null = Tool 'tia_hmi_set_button_script' @{ screen = 'MCPTEST_Screen'; button = 'BtnStart'; eventType = 'Tapped'; scriptCode = 'HMIRuntime.Trace("tap");' }
        $rm = Tool 'tia_hmi_remove_event_script' @{ screen = 'MCPTEST_Screen'; item = 'BtnStart'; eventType = 'Tapped'; confirm = $true }
        Check '  removing a script returns it' ($rm.Data.previousScript -match 'tap')
        $null = Tool 'tia_hmi_bind_property_to_tag' @{ screen = 'MCPTEST_Screen'; item = 'Box'; property = 'Visible'; tag = 'MCPTEST_Flag' }
        $null = Tool 'tia_hmi_set_property_tag_color' @{ screen = 'MCPTEST_Screen'; item = 'Box'; property = 'BackColor'; tag = 'MCPTEST_Flag'; falseColor = 'Gray'; trueColor = 'Red' }
        $col = Tool 'tia_dump_property_dynamization_colors' @{ screen = 'MCPTEST_Screen'; item = 'Box'; property = 'BackColor' }
        Check '  the colour mapping reads back' (($col.Data.entries | ForEach-Object hex) -contains '#FF0000') ($col.Text -replace '\s+', ' ')
        $ps = Tool 'tia_hmi_set_property_script' @{ screen = 'MCPTEST_Screen'; item = 'Title'; property = 'Visible'; scriptCode = 'return Tags("MCPTEST_Flag").Read();'; trigger = 'T1s' }
        Check '  a property script takes its trigger' ($ps.Data.trigger -eq 'T1s') ($ps.Text -replace '\s+', ' ')
        $null = Tool 'tia_hmi_remove_property_dynamization' @{ screen = 'MCPTEST_Screen'; item = 'Title'; property = 'Visible'; confirm = $true }
        $use = Tool 'tia_hmi_find_tag_usage' @{ screen = 'MCPTEST_Screen'; filter = 'MCPTEST_Flag' }
        Check '  tag usage finds bindings and scripts' ($use.Data.count -ge 3 -and ($use.Data.uses.kind -contains 'EventScript')) ($use.Text -replace '\s+', ' ')
        $rep = Tool 'tia_hmi_replace_tag_refs' @{ screen = 'MCPTEST_Screen'; pattern = '^MCPTEST_Value$'; replacement = 'MCPTEST_Flag'; dryRun = $false; confirm = $true }
        Check '  replace rewrites the matching binding' ($rep.Data.count -eq 1 -and $rep.Data.applied) ($rep.Text -replace '\s+', ' ')
        $null = Tool 'tia_hmi_validate' @{ screen = 'MCPTEST_Screen' }
        $null = Tool 'tia_hmi_set_tag_acquisition_cycle' @{ tag = 'MCPTEST_Value'; cycle = 'T1s' } -Expect error -Match 'internal tag'
        if ($linked) {
            $cyc = Tool 'tia_hmi_set_tag_acquisition_cycle' @{ tag = 'MCPTEST_Linked'; cycle = 'T1s' }
            Check '  a PLC-linked tag takes a new cycle' ($cyc.Data.acquisitionCycle -eq 'T1s') ($cyc.Text -replace '\s+', ' ')
        }

        Section 'HMI: cleanup'
        $grouped = Tool 'tia_hmi_create_screen' @{ name = 'MCPTEST_Grouped'; group = 'MCPTEST_Group/Inner' }
        Check '  a screen can be created inside a screen group' ($grouped.Data.group -eq 'MCPTEST_Group/Inner') ($grouped.Text -replace '\s+', ' ')
        $null = Tool 'tia_hmi_get_screen' @{ screen = 'MCPTEST_Grouped' }
        $ungroup = Tool 'tia_hmi_delete_screen' @{ screen = 'MCPTEST_Grouped'; removeEmptyGroups = $true; confirm = $true }
        Check '  deleting it can take the empty groups along' (($ungroup.Data.groupsRemoved -join ',') -eq 'Inner,MCPTEST_Group') ($ungroup.Text -replace '\s+', ' ')

        $null = Tool 'tia_hmi_delete_screen_item' @{ screen = 'MCPTEST_Screen'; item = 'Box'; confirm = $true }
        $null = Tool 'tia_hmi_delete_screen' @{ screen = 'MCPTEST_Screen' } -Expect error -Match 'Confirmation required'
        $null = Tool 'tia_hmi_delete_screen' @{ screen = 'MCPTEST_Screen'; confirm = $true }
        $null = Tool 'tia_hmi_delete_tag' @{ tag = 'MCPTEST_Flag'; confirm = $true }
        $null = Tool 'tia_hmi_delete_tag' @{ tag = 'MCPTEST_Value'; confirm = $true }
        if ($linked) { $null = Tool 'tia_hmi_delete_tag' @{ tag = 'MCPTEST_Linked'; confirm = $true } }
        $leftTags = Tool 'tia_hmi_list_tags' @{ filter = 'MCPTEST' }
        $leftScreens = @((Tool 'tia_list_hmi_screens').Data.screens | Where-Object { $_.name -like 'MCPTEST*' })
        Check '  no MCPTEST tag or screen is left' ($leftTags.Data.count -eq 0 -and $leftScreens.Count -eq 0)
    }

    if ($Write) {
        Section 'Compile'
        if ($plc) {
            $sw = Tool 'tia_compile' @{ device = $plc } -TimeoutSec 900
            Check '  the PLC software compile returns a state and messages' ($sw.Data.state -and $sw.Data.messages.Count -gt 0) ($sw.Text -replace '\s+', ' ')
        }
        if ($hmi) {
            $hc = Tool 'tia_compile' @{ device = $hmi } -TimeoutSec 900
            Check '  the HMI compile returns a state and messages' ($hc.Data.state -and $hc.Data.messages.Count -gt 0) ($hc.Text -replace '\s+', ' ')
        }

        Section 'Project'
        $null = Tool 'tia_archive_project' @{ targetDir = $tmp } -Expect error -Match 'unsaved changes'
        Write-Host "`n  NOTE: the project was NOT saved. TIA now shows it as modified although every test object"
        Write-Host "        was removed again; close without saving to be back at exactly the previous state."
    }
}
finally {
    # Safety net: whatever happened above, do not leave test objects in the project.
    if ($Write -and -not $script:Server.HasExited) {
        try { Remove-TestObjects } catch { Write-Host "  cleanup failed: $_" -ForegroundColor Red }
    }
    $log = $script:Server.Stderr
    Stop-McpServer $script:Server
    Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue

    $warnings = @($log -split "`n" | Where-Object { $_ -match '\[WARN\]' -and $_ -notmatch 'refused' })
    if ($warnings.Count -gt 0) {
        Write-Host "`nServer warnings:"
        $warnings | Select-Object -Unique | Select-Object -First 30 | ForEach-Object { Write-Host "  $($_.Trim())" }
    }
}

Write-Host "`n$script:Passed passed, $script:Failed failed, $script:Skipped skipped"
exit ([int]($script:Failed -gt 0))
