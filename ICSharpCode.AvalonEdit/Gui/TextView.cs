// <file>
//     <copyright see="prj:///doc/copyright.txt"/>
//     <license see="prj:///doc/license.txt"/>
//     <author name="Daniel Grunwald"/>
//     <version>$Revision$</version>
// </file>

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.ComponentModel.Design;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.TextFormatting;
using System.Windows.Threading;

using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Utils;

namespace ICSharpCode.AvalonEdit.Gui
{
	/// <summary>
	/// A virtualizing panel producing+showing <see cref="VisualLine"/>s for a <see cref="TextDocument"/>.
	/// 
	/// This is the heart of the text editor, this class controls the text rendering process.
	/// 
	/// Taken as a standalone control, it's a text viewer without any editing capability.
	/// </summary>
	[System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1001:TypesThatOwnDisposableFieldsShouldBeDisposable",
	                                                 Justification = "The user usually doesn't work with TextView but with TextEditor; and nulling the Document property is sufficient to dispose everything.")]
	public class TextView : FrameworkElement, IScrollInfo, IWeakEventListener, IServiceProvider
	{
		#region Constructor
		static TextView()
		{
			ClipToBoundsProperty.OverrideMetadata(typeof(TextView), new FrameworkPropertyMetadata(Boxes.True));
		}
		
		/// <summary>
		/// Creates a new TextView instance.
		/// </summary>
		public TextView()
		{
			textLayer = new TextLayer(this);
			elementGenerators.CollectionChanged += elementGenerators_CollectionChanged;
			lineTransformers.CollectionChanged += lineTransformers_CollectionChanged;
			backgroundRenderer.CollectionChanged += backgroundRenderer_CollectionChanged;
			layers = new UIElementCollection(this, this);
			InsertLayer(textLayer, KnownLayer.Text, LayerInsertionPosition.Replace);
		}
		#endregion
		
		#region Document Property
		/// <summary>
		/// Document property.
		/// </summary>
		public static readonly DependencyProperty DocumentProperty
			= TextEditor.DocumentProperty.AddOwner(
				typeof(TextView), new FrameworkPropertyMetadata(OnDocumentChanged));
		
		TextDocument document;
		HeightTree heightTree;
		
		/// <summary>
		/// Gets/Sets the document displayed by the text editor.
		/// </summary>
		public TextDocument Document {
			get { return (TextDocument)GetValue(DocumentProperty); }
			set { SetValue(DocumentProperty, value); }
		}
		
		static void OnDocumentChanged(DependencyObject dp, DependencyPropertyChangedEventArgs e)
		{
			((TextView)dp).OnDocumentChanged((TextDocument)e.OldValue, (TextDocument)e.NewValue);
		}
		
		internal double FontSize {
			get {
				return (double)GetValue(TextBlock.FontSizeProperty);
			}
		}
		
		/// <summary>
		/// Occurs when the document property has changed.
		/// </summary>
		public event EventHandler DocumentChanged;
		
		void OnDocumentChanged(TextDocument oldValue, TextDocument newValue)
		{
			if (oldValue != null) {
				heightTree.Dispose();
				heightTree = null;
				formatter.Dispose();
				formatter = null;
				TextDocumentWeakEventManager.Changing.RemoveListener(oldValue, this);
			}
			this.document = newValue;
			ClearScrollData();
			ClearVisualLines();
			if (newValue != null) {
				TextDocumentWeakEventManager.Changing.AddListener(newValue, this);
				heightTree = new HeightTree(newValue, FontSize + 3);
				formatter = TextFormatter.Create();
			}
			InvalidateMeasure(DispatcherPriority.Normal);
			if (DocumentChanged != null)
				DocumentChanged(this, EventArgs.Empty);
		}
		
		bool IWeakEventListener.ReceiveWeakEvent(Type managerType, object sender, EventArgs e)
		{
			if (managerType == typeof(TextDocumentWeakEventManager.Changing)) {
				// put redraw into background so that other input events can be handled before the redraw
				DocumentChangeEventArgs change = (DocumentChangeEventArgs)e;
				Redraw(change.Offset, change.RemovalLength, DispatcherPriority.Background);
				return true;
			}
			return false;
		}
		#endregion
		
		#region ElementGenerators+LineTransformers Properties
		readonly ObservableCollection<VisualLineElementGenerator> elementGenerators = new ObservableCollection<VisualLineElementGenerator>();
		
		/// <summary>
		/// Gets a collection where element generators can be registered.
		/// </summary>
		public ObservableCollection<VisualLineElementGenerator> ElementGenerators {
			get { return elementGenerators; }
		}
		
		void elementGenerators_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
		{
			HandleTextViewConnect(e);
			Redraw();
		}
		
		readonly ObservableCollection<IVisualLineTransformer> lineTransformers = new ObservableCollection<IVisualLineTransformer>();
		
		/// <summary>
		/// Gets a collection where line transformers can be registered.
		/// </summary>
		public ObservableCollection<IVisualLineTransformer> LineTransformers {
			get { return lineTransformers; }
		}
		
		void lineTransformers_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
		{
			HandleTextViewConnect(e);
			Redraw();
		}
		#endregion
		
		#region Layers
		internal readonly TextLayer textLayer;
		readonly UIElementCollection layers;
		
		/// <summary>
		/// Gets the list of layers displayed in the text view.
		/// </summary>
		public UIElementCollection Layers {
			get { return layers; }
		}
		
		/// <summary>
		/// Inserts a new layer at a position specified relative to an existing layer.
		/// </summary>
		/// <param name="layer">The new layer to insert.</param>
		/// <param name="referencedLayer">The existing layer</param>
		/// <param name="position">Specifies whether</param>
		public void InsertLayer(UIElement layer, KnownLayer referencedLayer, LayerInsertionPosition position)
		{
			if (layer == null)
				throw new ArgumentNullException("layer");
			if (!Enum.IsDefined(typeof(KnownLayer), referencedLayer))
				throw new InvalidEnumArgumentException("referencedLayer", (int)referencedLayer, typeof(KnownLayer));
			if (!Enum.IsDefined(typeof(LayerInsertionPosition), position))
				throw new InvalidEnumArgumentException("position", (int)position, typeof(LayerInsertionPosition));
			if (referencedLayer == KnownLayer.Background && position != LayerInsertionPosition.Above)
				throw new InvalidOperationException("Cannot replace or insert below the background layer.");
			
			LayerPosition newPosition = new LayerPosition(referencedLayer, position);
			LayerPosition.SetLayerPosition(layer, newPosition);
			for (int i = 0; i < layers.Count; i++) {
				LayerPosition p = LayerPosition.GetLayerPosition(layers[i]);
				if (p != null) {
					if (p.KnownLayer == referencedLayer && p.Position == LayerInsertionPosition.Replace) {
						// found the referenced layer
						switch (position) {
							case LayerInsertionPosition.Below:
								layers.Insert(i, layer);
								return;
							case LayerInsertionPosition.Above:
								layers.Insert(i + 1, layer);
								return;
							case LayerInsertionPosition.Replace:
								layers[i] = layer;
								return;
						}
					} else if (p.KnownLayer == referencedLayer && p.Position == LayerInsertionPosition.Above
					           || p.KnownLayer > referencedLayer) {
						// we skipped the insertion position (referenced layer does not exist?)
						layers.Insert(i, layer);
						return;
					}
				}
			}
			// inserting after all existing layers:
			layers.Add(layer);
		}
		
		/// <inheritdoc/>
		protected override int VisualChildrenCount {
			get { return layers.Count; }
		}
		
		/// <inheritdoc/>
		protected override Visual GetVisualChild(int index)
		{
			return layers[index];
		}
		#endregion
		
		#region Redraw methods / VisualLine invalidation
		/// <summary>
		/// Causes the text editor to regenerate all visual lines.
		/// </summary>
		public void Redraw()
		{
			Redraw(DispatcherPriority.Normal);
		}
		
		/// <summary>
		/// Causes the text editor to regenerate all visual lines.
		/// </summary>
		public void Redraw(DispatcherPriority redrawPriority)
		{
			VerifyAccess();
			ClearVisualLines();
			InvalidateMeasure(redrawPriority);
		}
		
		/// <summary>
		/// Causes the text editor to regenerate the specified visual line.
		/// </summary>
		public void Redraw(VisualLine visualLine, DispatcherPriority redrawPriority)
		{
			VerifyAccess();
			if (allVisualLines.Remove(visualLine)) {
				visibleVisualLines = null;
				DisposeVisualLine(visualLine);
				InvalidateMeasure(redrawPriority);
			}
		}
		
		/// <summary>
		/// Causes the text editor to redraw all lines overlapping with the specified segment.
		/// </summary>
		public void Redraw(int offset, int length, DispatcherPriority redrawPriority)
		{
			VerifyAccess();
			bool removedLine = false;
			for (int i = 0; i < allVisualLines.Count; i++) {
				VisualLine visualLine = allVisualLines[i];
				int lineStart = visualLine.FirstDocumentLine.Offset;
				int lineEnd = visualLine.LastDocumentLine.Offset + visualLine.LastDocumentLine.TotalLength;
				if (!(lineEnd < offset || lineStart > offset + length)) {
					removedLine = true;
					allVisualLines.RemoveAt(i--);
					DisposeVisualLine(visualLine);
				}
			}
			if (removedLine) {
				visibleVisualLines = null;
				InvalidateMeasure(redrawPriority);
			}
		}
		
		/// <summary>
		/// Causes the text editor to redraw all lines overlapping with the specified segment.
		/// Does nothing if segment is null.
		/// </summary>
		public void Redraw(ISegment segment, DispatcherPriority redrawPriority)
		{
			if (segment != null) {
				Redraw(segment.Offset, segment.Length, redrawPriority);
			}
		}
		
		/// <summary>
		/// Invalidates all visual lines.
		/// The caller of ClearVisualLines() must also call InvalidateMeasure() to ensure
		/// that the visual lines will be recreated.
		/// </summary>
		void ClearVisualLines()
		{
			visibleVisualLines = null;
			if (allVisualLines.Count != 0) {
				foreach (VisualLine visualLine in allVisualLines) {
					DisposeVisualLine(visualLine);
				}
				allVisualLines.Clear();
			}
		}
		
		void DisposeVisualLine(VisualLine visualLine)
		{
			if (newVisualLines != null && newVisualLines.Contains(visualLine)) {
				throw new ArgumentException("Cannot dispose visual line because it is in construction!");
			}
			visualLine.IsDisposed = true;
			foreach (TextLine textLine in visualLine.TextLines) {
				textLine.Dispose();
			}
			textLayer.RemoveInlineObjects(visualLine);
		}
		#endregion
		
		#region InvalidateMeasure(DispatcherPriority)
		DispatcherOperation invalidateMeasureOperation;
		
		void InvalidateMeasure(DispatcherPriority priority)
		{
			if (priority >= DispatcherPriority.Render) {
				if (invalidateMeasureOperation != null) {
					invalidateMeasureOperation.Abort();
					invalidateMeasureOperation = null;
				}
				base.InvalidateMeasure();
			} else {
				if (invalidateMeasureOperation != null) {
					invalidateMeasureOperation.Priority = priority;
				} else {
					invalidateMeasureOperation = Dispatcher.BeginInvoke(
						priority,
						new Action(
							delegate {
								invalidateMeasureOperation = null;
								base.InvalidateMeasure();
							}
						)
					);
				}
			}
		}
		#endregion
		
		#region Get(OrConstruct)VisualLine
		/// <summary>
		/// Gets the visual line that contains the document line with the specified number.
		/// Returns null if the document line is outside the visible range.
		/// </summary>
		public VisualLine GetVisualLine(int documentLineNumber)
		{
			// TODO: EnsureVisualLines() ?
			foreach (VisualLine visualLine in allVisualLines) {
				Debug.Assert(visualLine.IsDisposed == false);
				int start = visualLine.FirstDocumentLine.LineNumber;
				int end = visualLine.LastDocumentLine.LineNumber;
				if (documentLineNumber >= start && documentLineNumber <= end)
					return visualLine;
			}
			return null;
		}
		
		/// <summary>
		/// Gets the visual line that contains the document line with the specified number.
		/// If that line is outside the visible range, a new VisualLine for that document line is constructed.
		/// </summary>
		public VisualLine GetOrConstructVisualLine(DocumentLine documentLine)
		{
			if (documentLine == null)
				throw new ArgumentNullException("documentLine");
			if (documentLine.Document != this.Document)
				throw new InvalidOperationException("Line belongs to wrong document");
			VerifyAccess();
			
			VisualLine l = GetVisualLine(documentLine.LineNumber);
			if (l == null) {
				TextRunProperties globalTextRunProperties = CreateGlobalTextRunProperties();
				TextParagraphProperties paragraphProperties = CreateParagraphProperties(globalTextRunProperties);
				
				while (heightTree.GetIsCollapsed(documentLine)) {
					documentLine = heightTree.GetLineByNumber(documentLine.LineNumber - 1);
				}
				
				l = BuildVisualLine(documentLine,
				                    globalTextRunProperties, paragraphProperties,
				                    elementGenerators.ToArray(), lineTransformers.ToArray(),
				                    lastAvailableSize);
				l.VisualTop = heightTree.GetVisualPosition(documentLine);
				allVisualLines.Add(l);
			}
			return l;
		}
		#endregion
		
		#region Visual Lines (fields and properties)
		List<VisualLine> allVisualLines = new List<VisualLine>();
		ReadOnlyCollection<VisualLine> visibleVisualLines;
		double clippedPixelsOnTop;
		List<VisualLine> newVisualLines;
		
		/// <summary>
		/// Gets the currently visible visual lines.
		/// </summary>
		/// <exception cref="VisualLinesInvalidException">
		/// Gets thrown if there are invalid visual lines when this property is accessed.
		/// You can use the <see cref="VisualLinesValid"/> property to check for this case,
		/// or use the <see cref="EnsureVisualLines()"/> method to force creating the visual lines
		/// when they are invalid.
		/// </exception>
		[System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1065:DoNotRaiseExceptionsInUnexpectedLocations")]
		public ReadOnlyCollection<VisualLine> VisualLines {
			get {
				if (visibleVisualLines == null)
					throw new VisualLinesInvalidException();
				return visibleVisualLines;
			}
		}
		
		/// <summary>
		/// Gets whether the visual lines are valid.
		/// Will return false after a call to Redraw(). Accessing the visual lines property
		/// will force immediate regeneration of valid lines.
		/// </summary>
		public bool VisualLinesValid {
			get { return visibleVisualLines != null; }
		}
		
		/// <summary>
		/// Occurs when the TextView was measured and changed its visual lines.
		/// </summary>
		public event EventHandler VisualLinesChanged;
		
		/// <summary>
		/// If the visual lines are invalid, creates new visual lines for the visible part
		/// of the document.
		/// If all visual lines are valid, this method does nothing.
		/// </summary>
		/// <exception cref="InvalidOperationException">The visual line build process is already running.
		/// It is not allowed to call this method during the construction of a visual line.</exception>
		public void EnsureVisualLines()
		{
			Dispatcher.VerifyAccess();
			if (inMeasure)
				throw new InvalidOperationException("The visual line build process is already running! Cannot EnsureVisualLines() during Measure!");
			if (visibleVisualLines == null) {
				// increase priority for re-measure
				InvalidateMeasure(DispatcherPriority.Normal);
				// force immediate re-measure
				UpdateLayout();
			}
		}
		#endregion
		
		#region Measure
		Size lastAvailableSize;
		bool inMeasure;
		
		/// <inheritdoc/>
		protected override Size MeasureOverride(Size availableSize)
		{
			if (!canHorizontallyScroll && !availableSize.Width.IsClose(lastAvailableSize.Width))
				ClearVisualLines();
			lastAvailableSize = availableSize;
			
			textLayer.RemoveInlineObjectsNow();
			
			foreach (UIElement layer in layers) {
				layer.Measure(availableSize);
			}
			InvalidateVisual(); // = InvalidateArrange+InvalidateRender
			textLayer.InvalidateVisual();
			
			if (document == null)
				return Size.Empty;
			
			double maxWidth;
			inMeasure = true;
			try {
				maxWidth = CreateAndMeasureVisualLines(availableSize);
			} finally {
				inMeasure = false;
			}
			
			textLayer.RemoveInlineObjectsNow();
			
			SetScrollData(availableSize,
			              new Size(maxWidth, heightTree.TotalHeight),
			              scrollOffset);
			if (VisualLinesChanged != null)
				VisualLinesChanged(this, EventArgs.Empty);
			if (canHorizontallyScroll) {
				return availableSize;
			} else {
				return new Size(maxWidth, availableSize.Height);
			}
		}
		
		/// <summary>
		/// Build all VisualLines in the visible range.
		/// </summary>
		/// <returns>Width the longest line</returns>
		double CreateAndMeasureVisualLines(Size availableSize)
		{
			TextRunProperties globalTextRunProperties = CreateGlobalTextRunProperties();
			TextParagraphProperties paragraphProperties = CreateParagraphProperties(globalTextRunProperties);
			
			Debug.WriteLine("Measure availableSize=" + availableSize + ", scrollOffset=" + scrollOffset);
			var firstLineInView = heightTree.GetLineByVisualPosition(scrollOffset.Y);
			
			// number of pixels clipped from the first visual line(s)
			clippedPixelsOnTop = scrollOffset.Y - heightTree.GetVisualPosition(firstLineInView);
			Debug.Assert(clippedPixelsOnTop >= 0);
			
			newVisualLines = new List<VisualLine>();
			
			var elementGeneratorsArray = elementGenerators.ToArray();
			var lineTransformersArray = lineTransformers.ToArray();
			var nextLine = firstLineInView;
			double maxWidth = 0;
			double yPos = -clippedPixelsOnTop;
			while (yPos < availableSize.Height && nextLine != null) {
				VisualLine visualLine = GetVisualLine(nextLine.LineNumber);
				if (visualLine == null) {
					visualLine = BuildVisualLine(nextLine,
					                             globalTextRunProperties, paragraphProperties,
					                             elementGeneratorsArray, lineTransformersArray,
					                             availableSize);
				}
				
				visualLine.VisualTop = scrollOffset.Y + yPos;
				
				nextLine = visualLine.LastDocumentLine.NextLine;
				
				yPos += visualLine.Height;
				
				foreach (TextLine textLine in visualLine.TextLines) {
					if (textLine.WidthIncludingTrailingWhitespace > maxWidth)
						maxWidth = textLine.WidthIncludingTrailingWhitespace;
				}
				
				newVisualLines.Add(visualLine);
			}
			
			foreach (VisualLine line in allVisualLines) {
				Debug.Assert(line.IsDisposed == false);
				if (!newVisualLines.Contains(line))
					DisposeVisualLine(line);
			}
			
			allVisualLines = newVisualLines;
			// visibleVisualLines = readonly copy of visual lines
			visibleVisualLines = new ReadOnlyCollection<VisualLine>(newVisualLines.ToArray());
			newVisualLines = null;
			
			if (allVisualLines.Any(line => line.IsDisposed)) {
				throw new InvalidOperationException("A visual line was disposed even though it is still in use.\n" +
				                                    "This can happen when Redraw() is called during measure for lines " +
				                                    "that are already constructed.");
			}
			return maxWidth;
		}
		#endregion
		
		#region BuildVisualLine
		TextFormatter formatter;
		
		TextRunProperties CreateGlobalTextRunProperties()
		{
			return new GlobalTextRunProperties {
				typeface = this.CreateTypeface(),
				fontRenderingEmSize = FontSize,
				foregroundBrush = (Brush)GetValue(Control.ForegroundProperty),
				cultureInfo = CultureInfo.CurrentCulture
			};
		}
		
		TextParagraphProperties CreateParagraphProperties(TextRunProperties defaultTextRunProperties)
		{
			return new VisualLineTextParagraphProperties {
				defaultTextRunProperties = defaultTextRunProperties,
				textWrapping = canHorizontallyScroll ? TextWrapping.NoWrap : TextWrapping.Wrap,
				tabSize = 4 * WideSpaceWidth
			};
		}
		
		VisualLine BuildVisualLine(DocumentLine documentLine,
		                           TextRunProperties globalTextRunProperties,
		                           TextParagraphProperties paragraphProperties,
		                           VisualLineElementGenerator[] elementGeneratorsArray,
		                           IVisualLineTransformer[] lineTransformersArray,
		                           Size availableSize)
		{
			if (heightTree.GetIsCollapsed(documentLine))
				throw new InvalidOperationException("Trying to build visual line from collapsed line");
			
			Debug.WriteLine("Building line " + documentLine.LineNumber);
			
			VisualLine visualLine = new VisualLine(this, documentLine);
			VisualLineTextSource textSource = new VisualLineTextSource(visualLine) {
				Document = document,
				GlobalTextRunProperties = globalTextRunProperties,
				TextView = this
			};
			
			visualLine.ConstructVisualElements(textSource, elementGeneratorsArray);
			
			#if DEBUG
			for (int i = visualLine.FirstDocumentLine.LineNumber + 1; i <= visualLine.LastDocumentLine.LineNumber; i++) {
				if (!heightTree.GetIsCollapsed(document.GetLineByNumber(i)))
					throw new InvalidOperationException("Line " + i + " was skipped by a VisualLineElementGenerator, but it is not collapsed.");
			}
			#endif
			
			visualLine.RunTransformers(textSource, lineTransformersArray);
			
			// now construct textLines:
			int textOffset = 0;
			TextLineBreak lastLineBreak = null;
			var textLines = new List<TextLine>();
			while (textOffset <= visualLine.VisualLength) {
				TextLine textLine = formatter.FormatLine(
					textSource,
					textOffset,
					availableSize.Width,
					paragraphProperties,
					lastLineBreak
				);
				textLines.Add(textLine);
				textOffset += textLine.Length;
				
				lastLineBreak = textLine.GetTextLineBreak();
			}
			visualLine.SetTextLines(textLines);
			heightTree.SetHeight(visualLine.FirstDocumentLine, visualLine.Height);
			return visualLine;
		}
		#endregion
		
		#region Arrange
		/// <summary>
		/// Arrange implementation.
		/// </summary>
		protected override Size ArrangeOverride(Size finalSize)
		{
			if (document == null || allVisualLines.Count == 0)
				return finalSize;
			
			// validate scroll position
			Vector newScrollOffset = scrollOffset;
			if (scrollOffset.X + finalSize.Width > scrollExtent.Width) {
				newScrollOffset.X = Math.Max(0, scrollExtent.Width - finalSize.Width);
			}
			if (scrollOffset.Y + finalSize.Height > scrollExtent.Height) {
				newScrollOffset.Y = Math.Max(0, scrollExtent.Height - finalSize.Height);
			}
			if (SetScrollData(scrollViewport, scrollExtent, newScrollOffset))
				InvalidateMeasure(DispatcherPriority.Normal);
			
			//Debug.WriteLine("Arrange finalSize=" + finalSize + ", scrollOffset=" + scrollOffset);
			
//			double maxWidth = 0;
			
			foreach (UIElement adorner in layers) {
				adorner.Arrange(new Rect(new Point(0, 0), finalSize));
			}
			
			if (visibleVisualLines != null) {
				Point pos = new Point(-scrollOffset.X, -clippedPixelsOnTop);
				foreach (VisualLine visualLine in visibleVisualLines) {
					int offset = 0;
					foreach (TextLine textLine in visualLine.TextLines) {
						foreach (var span in textLine.GetTextRunSpans()) {
							InlineObjectRun inline = span.Value as InlineObjectRun;
							if (inline != null && inline.VisualLine != null) {
								Debug.Assert(textLayer.inlineObjects.Contains(inline));
								double distance = textLine.GetDistanceFromCharacterHit(new CharacterHit(offset, 0));
								inline.Element.Arrange(new Rect(new Point(pos.X + distance, pos.Y), inline.Element.DesiredSize));
							}
							offset += span.Length;
						}
						pos.Y += textLine.Height;
					}
				}
			}
			InvalidateCursor();
			
			return finalSize;
		}
		#endregion
		
		#region Render
		readonly ObservableCollection<IBackgroundRenderer> backgroundRenderer = new ObservableCollection<IBackgroundRenderer>();
		
		/// <summary>
		/// Gets the list of background renderers.
		/// </summary>
		public ObservableCollection<IBackgroundRenderer> BackgroundRenderer {
			get { return backgroundRenderer; }
		}
		
		void backgroundRenderer_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
		{
			HandleTextViewConnect(e);
			InvalidateVisual();
			foreach (UIElement layer in this.Layers) {
				// invalidate known layers
				if (layer is Layer)
					layer.InvalidateVisual();
			}
		}
		
		/// <inheritdoc/>
		protected override void OnRender(DrawingContext drawingContext)
		{
			RenderBackground(drawingContext, KnownLayer.Background);
		}
		
		internal void RenderBackground(DrawingContext drawingContext, KnownLayer layer)
		{
			foreach (IBackgroundRenderer bg in backgroundRenderer) {
				if (bg.Layer == layer) {
					bg.Draw(drawingContext);
				}
			}
		}
		
		internal void RenderTextLayer(DrawingContext drawingContext)
		{
			Point pos = new Point(-scrollOffset.X, -clippedPixelsOnTop);
			foreach (VisualLine visualLine in allVisualLines) {
				foreach (TextLine textLine in visualLine.TextLines) {
					textLine.Draw(drawingContext, pos, InvertAxes.None);
					pos.Y += textLine.Height;
				}
			}
		}
		#endregion
		
		#region IScrollInfo implementation
		/// <summary>
		/// Size of the document, in pixels.
		/// </summary>
		Size scrollExtent;
		
		/// <summary>
		/// Offset of the scroll position.
		/// </summary>
		Vector scrollOffset;
		
		/// <summary>
		/// Size of the viewport.
		/// </summary>
		Size scrollViewport;
		
		void ClearScrollData()
		{
			SetScrollData(new Size(), new Size(), new Vector());
		}
		
		bool SetScrollData(Size viewport, Size extent, Vector offset)
		{
			if (!(viewport.IsClose(this.scrollViewport)
			      && extent.IsClose(this.scrollExtent)
			      && offset.IsClose(this.scrollOffset)))
			{
				this.scrollViewport = viewport;
				this.scrollExtent = extent;
				SetScrollOffset(offset);
				this.OnScrollChange();
				return true;
			}
			return false;
		}
		
		void OnScrollChange()
		{
			ScrollViewer scrollOwner = ((IScrollInfo)this).ScrollOwner;
			if (scrollOwner != null) {
				scrollOwner.InvalidateScrollInfo();
			}
		}
		
		bool canVerticallyScroll;
		bool IScrollInfo.CanVerticallyScroll {
			get { return canVerticallyScroll; }
			set {
				if (canVerticallyScroll != value) {
					canVerticallyScroll = value;
					InvalidateMeasure(DispatcherPriority.Normal);
				}
			}
		}
		bool canHorizontallyScroll;
		bool IScrollInfo.CanHorizontallyScroll {
			get { return canHorizontallyScroll; }
			set {
				if (canHorizontallyScroll != value) {
					canHorizontallyScroll = value;
					ClearVisualLines();
					InvalidateMeasure(DispatcherPriority.Normal);
				}
			}
		}
		
		double IScrollInfo.ExtentWidth {
			get { return scrollExtent.Width; }
		}
		
		double IScrollInfo.ExtentHeight {
			get { return scrollExtent.Height; }
		}
		
		double IScrollInfo.ViewportWidth {
			get { return scrollViewport.Width; }
		}
		
		double IScrollInfo.ViewportHeight {
			get { return scrollViewport.Height; }
		}
		
		/// <summary>
		/// Gets the horizontal scroll offset.
		/// </summary>
		public double HorizontalOffset {
			get { return scrollOffset.X; }
		}
		
		/// <summary>
		/// Gets the vertical scroll offset.
		/// </summary>
		public double VerticalOffset {
			get { return scrollOffset.Y; }
		}
		
		/// <summary>
		/// Gets the scroll offset;
		/// </summary>
		public Vector ScrollOffset {
			get { return scrollOffset; }
		}
		
		/// <summary>
		/// Occurs when the scroll offset has changed.
		/// </summary>
		public event EventHandler ScrollOffsetChanged;
		
		void SetScrollOffset(Vector vector)
		{
			if (!scrollOffset.IsClose(vector)) {
				scrollOffset = vector;
				if (ScrollOffsetChanged != null)
					ScrollOffsetChanged(this, EventArgs.Empty);
			}
		}
		
		ScrollViewer IScrollInfo.ScrollOwner { get; set; }
		
		void IScrollInfo.LineUp()
		{
			((IScrollInfo)this).SetVerticalOffset(scrollOffset.Y - FontSize);
		}
		
		void IScrollInfo.LineDown()
		{
			((IScrollInfo)this).SetVerticalOffset(scrollOffset.Y + FontSize);
		}
		
		void IScrollInfo.LineLeft()
		{
			((IScrollInfo)this).SetHorizontalOffset(scrollOffset.X - WideSpaceWidth);
		}
		
		void IScrollInfo.LineRight()
		{
			((IScrollInfo)this).SetHorizontalOffset(scrollOffset.X + WideSpaceWidth);
		}
		
		void IScrollInfo.PageUp()
		{
			((IScrollInfo)this).SetVerticalOffset(scrollOffset.Y - scrollViewport.Height);
		}
		
		void IScrollInfo.PageDown()
		{
			((IScrollInfo)this).SetVerticalOffset(scrollOffset.Y + scrollViewport.Height);
		}
		
		void IScrollInfo.PageLeft()
		{
			((IScrollInfo)this).SetHorizontalOffset(scrollOffset.X - scrollViewport.Width);
		}
		
		void IScrollInfo.PageRight()
		{
			((IScrollInfo)this).SetHorizontalOffset(scrollOffset.X + scrollViewport.Width);
		}
		
		void IScrollInfo.MouseWheelUp()
		{
			((IScrollInfo)this).SetVerticalOffset(
				scrollOffset.Y - (SystemParameters.WheelScrollLines * FontSize));
			OnScrollChange();
		}
		
		void IScrollInfo.MouseWheelDown()
		{
			((IScrollInfo)this).SetVerticalOffset(
				scrollOffset.Y + (SystemParameters.WheelScrollLines * FontSize));
			OnScrollChange();
		}
		
		void IScrollInfo.MouseWheelLeft()
		{
			((IScrollInfo)this).SetHorizontalOffset(
				scrollOffset.X - (SystemParameters.WheelScrollLines * WideSpaceWidth));
			OnScrollChange();
		}
		
		void IScrollInfo.MouseWheelRight()
		{
			((IScrollInfo)this).SetHorizontalOffset(
				scrollOffset.X + (SystemParameters.WheelScrollLines * WideSpaceWidth));
			OnScrollChange();
		}
		
		double WideSpaceWidth {
			get {
				return FontSize / 2;
			}
		}
		
		static double ValidateVisualOffset(double offset)
		{
			if (double.IsNaN(offset))
				throw new ArgumentException("offset must not be NaN");
			if (offset < 0)
				return 0;
			else
				return offset;
		}
		
		void IScrollInfo.SetHorizontalOffset(double offset)
		{
			offset = ValidateVisualOffset(offset);
			if (!scrollOffset.X.IsClose(offset)) {
				SetScrollOffset(new Vector(offset, scrollOffset.Y));
				InvalidateVisual();
				textLayer.InvalidateVisual();
			}
		}
		
		void IScrollInfo.SetVerticalOffset(double offset)
		{
			offset = ValidateVisualOffset(offset);
			if (!scrollOffset.Y.IsClose(offset)) {
				SetScrollOffset(new Vector(scrollOffset.X, offset));
				InvalidateMeasure(DispatcherPriority.Normal);
			}
		}
		
		Rect IScrollInfo.MakeVisible(Visual visual, Rect rectangle)
		{
			if (rectangle.IsEmpty || visual == null || visual == this || !this.IsAncestorOf(visual)) {
				return Rect.Empty;
			}
			// Convert rectangle into our coordinate space.
			GeneralTransform childTransform = visual.TransformToAncestor(this);
			rectangle = childTransform.TransformBounds(rectangle);
			
			MakeVisible(rectangle);
			
			return rectangle;
		}
		
		/// <summary>
		/// Scrolls the text view so that the specified rectangle gets visible.
		/// </summary>
		public void MakeVisible(Rect rectangle)
		{
			Rect visibleRectangle = new Rect(scrollOffset.X, scrollOffset.Y,
			                                 scrollViewport.Width, scrollViewport.Height);
			Vector newScrollOffset = scrollOffset;
			if (rectangle.Left < visibleRectangle.Left) {
				if (rectangle.Right > visibleRectangle.Right) {
					newScrollOffset.X = rectangle.Left + rectangle.Width / 2;
				} else {
					newScrollOffset.X = rectangle.Left;
				}
			} else if (rectangle.Right > visibleRectangle.Right) {
				newScrollOffset.X = rectangle.Right - scrollViewport.Width;
			}
			if (rectangle.Top < visibleRectangle.Top) {
				if (rectangle.Bottom > visibleRectangle.Bottom) {
					newScrollOffset.Y = rectangle.Top + rectangle.Height / 2;
				} else {
					newScrollOffset.Y = rectangle.Top;
				}
			} else if (rectangle.Bottom > visibleRectangle.Bottom) {
				newScrollOffset.Y = rectangle.Bottom - scrollViewport.Height;
			}
			newScrollOffset.X = ValidateVisualOffset(newScrollOffset.X);
			newScrollOffset.Y = ValidateVisualOffset(newScrollOffset.Y);
			if (!scrollOffset.IsClose(newScrollOffset)) {
				SetScrollOffset(newScrollOffset);
				this.OnScrollChange();
				InvalidateMeasure(DispatcherPriority.Normal);
			}
		}
		#endregion
		
		#region Visual element mouse handling
		/// <inheritdoc/>
		protected override HitTestResult HitTestCore(PointHitTestParameters hitTestParameters)
		{
			// accept clicks even where the text area draws no background
			return new PointHitTestResult(this, hitTestParameters.HitPoint);
		}
		
		[ThreadStatic] static bool invalidCursor;
		
		/// <summary>
		/// Updates the mouse cursor by calling <see cref="Mouse.UpdateCursor"/>, but with input priority.
		/// </summary>
		public static void InvalidateCursor()
		{
			if (!invalidCursor) {
				invalidCursor = true;
				Dispatcher.CurrentDispatcher.BeginInvoke(
					DispatcherPriority.Input,
					new Action(
						delegate {
							invalidCursor = false;
							Mouse.UpdateCursor();
						}));
			}
		}
		
		/// <inheritdoc/>
		protected override void OnQueryCursor(QueryCursorEventArgs e)
		{
			VisualLineElement element = GetVisualLineElementFromPosition(e.GetPosition(this) + scrollOffset);
			if (element != null) {
				element.OnQueryCursor(e);
			}
		}
		
		/// <inheritdoc/>
		protected override void OnMouseDown(MouseButtonEventArgs e)
		{
			base.OnMouseDown(e);
			if (!e.Handled) {
				EnsureVisualLines();
				VisualLineElement element = GetVisualLineElementFromPosition(e.GetPosition(this) + scrollOffset);
				if (element != null) {
					element.OnMouseDown(e);
				}
			}
		}
		
		/// <inheritdoc/>
		protected override void OnMouseUp(MouseButtonEventArgs e)
		{
			base.OnMouseUp(e);
			if (!e.Handled) {
				EnsureVisualLines();
				VisualLineElement element = GetVisualLineElementFromPosition(e.GetPosition(this) + scrollOffset);
				if (element != null) {
					element.OnMouseUp(e);
				}
			}
		}
		#endregion
		
		#region Getting elements from Visual Position
		/// <summary>
		/// Gets the visual line at the specified document position (relative to start of document).
		/// Returns null if there is no visual line for the position (e.g. the position is outside the visible
		/// text area).
		/// </summary>
		public VisualLine GetVisualLineFromVisualTop(double visualTop)
		{
			EnsureVisualLines();
			foreach (VisualLine vl in this.VisualLines) {
				if (visualTop < vl.VisualTop)
					continue;
				if (visualTop < vl.VisualTop + vl.Height)
					return vl;
			}
			return null;
		}
		
		VisualLineElement GetVisualLineElementFromPosition(Point visualPosition)
		{
			VisualLine vl = GetVisualLineFromVisualTop(visualPosition.Y);
			if (vl != null) {
				int column = vl.GetVisualColumn(visualPosition);
//				Debug.WriteLine(vl.FirstDocumentLine.LineNumber + " vc " + column);
				foreach (VisualLineElement element in vl.Elements) {
					if (element.VisualColumn + element.VisualLength < column)
						continue;
					return element;
				}
			}
			return null;
		}
		#endregion
		
		#region Visual Position <-> TextViewPosition
		/// <summary>
		/// Gets the visual position from a text view position.
		/// </summary>
		/// <param name="position">The text view position.</param>
		/// <param name="yPositionMode">The mode how to retrieve the Y position.</param>
		/// <returns>The position in WPF device-independent pixels relative
		/// to the top left corner of the document.</returns>
		public Point GetVisualPosition(TextViewPosition position, VisualYPosition yPositionMode)
		{
			VerifyAccess();
			if (this.Document == null)
				throw new InvalidOperationException("There is no document assigned to the TextView");
			DocumentLine documentLine = this.Document.GetLineByNumber(position.Line);
			VisualLine visualLine = GetOrConstructVisualLine(documentLine);
			int visualColumn = position.VisualColumn;
			if (visualColumn < 0) {
				int offset = documentLine.Offset + position.Column - 1;
				visualColumn = visualLine.GetVisualColumn(offset - visualLine.FirstDocumentLine.Offset);
			}
			return visualLine.GetVisualPosition(visualColumn, yPositionMode);
		}
		#endregion
		
		#region Service Provider
		readonly ServiceContainer services = new ServiceContainer();
		
		/// <summary>
		/// Gets a service container used to associate services with the text view.
		/// </summary>
		public ServiceContainer Services {
			get { return services; }
		}
		
		object IServiceProvider.GetService(Type serviceType)
		{
			return services.GetService(serviceType);
		}
		
		void HandleTextViewConnect(NotifyCollectionChangedEventArgs e)
		{
			switch (e.Action) {
				case NotifyCollectionChangedAction.Add:
				case NotifyCollectionChangedAction.Remove:
				case NotifyCollectionChangedAction.Replace:
					if (e.OldItems != null) {
						foreach (ITextViewConnect c in e.OldItems.OfType<ITextViewConnect>())
							c.RemoveFromTextView(this);
					}
					if (e.NewItems != null) {
						foreach (ITextViewConnect c in e.NewItems.OfType<ITextViewConnect>())
							c.AddToTextView(this);
					}
					break;
				case NotifyCollectionChangedAction.Move:
					// ignore Move
					break;
				default:
					throw new NotSupportedException(e.Action.ToString());
			}
		}
		#endregion
		
		/// <summary>
		/// Collapses lines for the purpose of scrolling. This method is meant for
		/// <see cref="VisualLineElementGenerator"/>s that cause <see cref="VisualLine"/>s to span
		/// multiple <see cref="DocumentLine"/>s. Do not call it without providing a corresponding
		/// <see cref="VisualLineElementGenerator"/>.
		/// If you want to create collapsible text sections, see <see cref="FoldingManager"/>.
		/// </summary>
		public CollapsedLineSection CollapseLines(DocumentLine start, DocumentLine end)
		{
			VerifyAccess();
			return heightTree.CollapseText(start, end);
		}
		
		/// <summary>
		/// Gets the height of the document.
		/// </summary>
		public double DocumentHeight {
			get { return heightTree.TotalHeight; }
		}
		
		/// <summary>
		/// Gets the document line at the specified visual position.
		/// </summary>
		public DocumentLine GetDocumentLineByVisualTop(double visualTop)
		{
			VerifyAccess();
			if (heightTree == null)
				throw new InvalidOperationException();
			return heightTree.GetLineByVisualPosition(visualTop);
		}
	}
}
