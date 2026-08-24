import { useCallback } from 'react';
import { useEditorConfig } from './useEditorConfig';
import { downloadTextFile } from '../lib/export';

/**
 * The one seam every editor download goes through: the host's
 * <see>EditorConfig.saveFile</see> when provided (a desktop shell's native save
 * dialog), the DOM anchor-download otherwise. Grid and navigation exports both
 * call this, so a host that supplies a saver covers every download at once.
 */
export function useSaveFile(): (filename: string, content: string, mime: string) => Promise<void> {
    const { saveFile } = useEditorConfig();
    return useCallback(async (filename: string, content: string, mime: string) => {
        if (saveFile) {
            await saveFile({ filename, content, mime });
            return;
        }
        downloadTextFile(content, filename, mime);
    }, [saveFile]);
}
