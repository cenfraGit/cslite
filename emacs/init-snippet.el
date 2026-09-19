;--------------------------------------------------------------------------------
; cslite
; c# language server
;--------------------------------------------------------------------------------

;; Both halves live in your Emacs configuration, so no machine needs to know
;; where the server's source checkout is:
;;
;;   lisp/cslite.el   the eglot glue, committed with your config
;;   cslite/          the published binary, ignored by git, copied per machine
;;
;; Install or update both from the server repository:
;;
;;   install.cmd     (Windows)        install.sh     (Linux)
;;
;; M-x cslite-where reports which binary was found.

(use-package eglot
  :ensure nil
  :init
  (add-to-list 'load-path (expand-file-name "lisp" user-emacs-directory))
  :custom
  ;; Stop the server once its last C# buffer is gone.
  (eglot-autoshutdown t)
  ;; Let M-. keep working after it has jumped outside the project.
  (eglot-extend-to-xref t)
  ;; Keep a short protocol trace for when something misbehaves.
  (eglot-events-buffer-config '(:size 2000 :format short))
  ;; Show the documentation under the signature, not just the first line, but
  ;; cap it: an unclosed paren is a syntax error, so the signature you want and
  ;; a "')' expected" from flymake arrive on the same line and the echo area
  ;; grows to fit both.
  (eldoc-echo-area-use-multiline-p 3)
  ;; Once the doc buffer is on screen, send the full text there and leave the
  ;; echo area for one-liners. C-c l d opens it.
  (eldoc-echo-area-prefer-doc-buffer t)
  ;; Room for those three lines; the default clips at a quarter of the frame.
  (max-mini-window-height 0.3)
  ;; Eglot answers fast enough that the default half-second feels sluggish.
  (eldoc-idle-delay 0.2)
  :config
  (require 'cslite)
  (cslite-setup)
)

;------------------------------------------------------------ flymake

(use-package flymake
  :ensure nil
  :bind (:map flymake-mode-map
              ("C-c e n" . flymake-goto-next-error)
              ("C-c e p" . flymake-goto-prev-error)
              ("C-c e l" . flymake-show-buffer-diagnostics))
)

;------------------------------------------------------------ keybindings

;; xref already binds M-. and M-? ; these are the rest.
(global-set-key (kbd "C-c l a") #'eglot-code-actions)
;; With consult installed, consult-imenu is the nicer one to bind here.
(global-set-key (kbd "C-c l w") #'xref-find-apropos)
(global-set-key (kbd "C-c l i") #'imenu)
(global-set-key (kbd "C-c l d") #'eldoc-doc-buffer)
(global-set-key (kbd "C-c l o") #'cslite-signatures)
(global-set-key (kbd "C-c l n") #'eglot-rename)
(global-set-key (kbd "C-c l r") #'cslite-restart)
(global-set-key (kbd "C-c l e") #'eglot-events-buffer)
;; Windows will not let you overwrite the server DLL while it is running, so
;; stop it before reinstalling a new build.
(global-set-key (kbd "C-c l s") #'eglot-shutdown)
